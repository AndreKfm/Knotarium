using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Knotarium.Features.Execution;
using Knotarium.Infrastructure.Persistence;
using Xunit;

namespace Knotarium.Tests.Execution;

public class JournalFoldTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;
    private readonly INodePackageManifestProvider _manifestProvider = new InMemoryNodePackageManifestProvider();

    public JournalFoldTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private async Task<AppDbContext> CreateContextAsync()
    {
        var context = new AppDbContext(_dbContextOptions);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    // Mock Registry & Task Implementation
    private class MockNodeTaskRegistry : INodeTaskRegistry
    {
        private readonly Dictionary<string, INodeTask> _registry = new(StringComparer.OrdinalIgnoreCase);

        public void Register(string type, INodeTask task)
        {
            _registry[type] = task;
        }

        public INodeTask? GetTask(string nodeType)
        {
            return _registry.TryGetValue(nodeType, out var task) ? task : null;
        }
    }

    private class FunctionalNodeTask : INodeTask
    {
        private readonly Func<NodeExecutionContext, Task<LegacyNodeResult>> _func;

        public FunctionalNodeTask(Func<NodeExecutionContext, Task<LegacyNodeResult>> func)
        {
            _func = func;
        }

        public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
        {
            return _func(context);
        }
    }

    private class FakeEventPublisher : IExecutionEventPublisher
    {
        public Task PublishAsync(ExecutionInstanceId executionId, ExecutionJournal entry, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TransactionalSuspension_AtomicityRollbackOnFailure_NoStateSaved()
    {
        // Arrange
        using var context = await CreateContextAsync();

        // 1. Setup workflow
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var delayNode = new NodeDefinition(NodeId.Create("delay"), "delay", new Dictionary<string, object>());
        var endNode = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());

        var edge1 = new EdgeDefinition("e1", startNode.Id, "result", delayNode.Id, "in");
        var edge2 = new EdgeDefinition("e2", delayNode.Id, "result", endNode.Id, "in");

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Atomicity Test Workflow",
            new[] { startNode, delayNode, endNode },
            new[] { edge1, edge2 }
        );

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));
        registry.Register("delay", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.WaitForEvent("webhook"))));

        // Create initial pending instance
        var instanceId = ExecutionInstanceId.New();
        var instance = new ExecutionInstance
        {
            Id = instanceId,
            WorkflowDefinitionId = workflowId,
            Status = ExecutionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            GlobalVariables = new Dictionary<string, object> { ["foo"] = "bar" }
        };
        context.ExecutionInstances.Add(instance);
        await context.SaveChangesAsync();

        // 2. Inject DB constraint failure.
        // We use a separate DbContext instance to insert a NodeState into the database connection
        // so that the main context is unaware of it.
        var duplicateStateId = Guid.NewGuid();
        using (var setupContext = new AppDbContext(_dbContextOptions))
        {
            var dbNodeState = new NodeState
            {
                Id = duplicateStateId,
                ExecutionInstanceId = instanceId,
                NodeId = NodeId.Create("db-one"),
                Status = NodeStatus.Pending
            };
            setupContext.NodeStates.Add(dbNodeState);
            await setupContext.SaveChangesAsync();
        }

        // Now, we add a NodeState with the same Id to the executor's context.
        // It will be tracked as "Added". When the executor calls SaveChangesAsync,
        // EF Core will attempt to execute an INSERT SQL statement, which will fail 
        // with a SQLite UNIQUE constraint violation because the ID already exists in SQLite.
        var trackedDuplicate = new NodeState
        {
            Id = duplicateStateId,
            ExecutionInstanceId = instanceId,
            NodeId = NodeId.Create("duplicate"),
            Status = NodeStatus.Pending
        };
        context.NodeStates.Add(trackedDuplicate);

        var provider = new SqliteWorkflowDefinitionProvider(context);
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        // Act & Assert
        // The exception should be thrown because save changes failed due to duplicate key constraint
        await Assert.ThrowsAnyAsync<Exception>(() => executor.ExecuteAsync(instanceId));

        // Verify Rollback
        using var readContext = new AppDbContext(_dbContextOptions);
        var retrievedInstance = await readContext.ExecutionInstances
            .Include(e => e.JournalEntries)
            .FirstOrDefaultAsync(e => e.Id == instanceId);

        Assert.NotNull(retrievedInstance);
        // Status must remain Pending (not Suspended)
        Assert.Equal(ExecutionStatus.Pending, retrievedInstance.Status);
        // VariableState should NOT be set
        Assert.Null(retrievedInstance.VariableState);
        // No suspension journal entries should exist
        Assert.DoesNotContain(retrievedInstance.JournalEntries, j => j.EventType == JournalEventTypes.WorkflowSuspended);
    }

    [Fact]
    public async Task CanonicalRehydration_FoldValidatesSoleSourceOfTruth()
    {
        // Arrange
        using var context = await CreateContextAsync();

        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var delayNode = new NodeDefinition(NodeId.Create("delay"), "delay", new Dictionary<string, object>());

        var edge = new EdgeDefinition("e1", startNode.Id, "result", delayNode.Id, "in");

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Fold Test Workflow",
            new[] { startNode, delayNode },
            new[] { edge }
        );

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));
        registry.Register("delay", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.WaitForEvent("wait_event"))));

        var instanceId = ExecutionInstanceId.New();
        var instance = new ExecutionInstance
        {
            Id = instanceId,
            WorkflowDefinitionId = workflowId,
            Status = ExecutionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            GlobalVariables = new Dictionary<string, object> { ["user_id"] = 42, ["role"] = "admin" }
        };
        context.ExecutionInstances.Add(instance);
        await context.SaveChangesAsync();

        var provider = new SqliteWorkflowDefinitionProvider(context);
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        // Act - Trigger suspension
        await executor.ExecuteAsync(instanceId);

        // Assert - Verify projection cache is written correctly
        using var verifyContext = new AppDbContext(_dbContextOptions);
        var instSuspended = await verifyContext.ExecutionInstances
            .Include(e => e.JournalEntries)
            .FirstAsync(e => e.Id == instanceId);

        Assert.Equal(ExecutionStatus.Suspended, instSuspended.Status);
        Assert.NotNull(instSuspended.VariableState);

        var preDeletedVars = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(instSuspended.VariableState);
        Assert.NotNull(preDeletedVars);
        Assert.Equal(42, preDeletedVars["user_id"].GetInt32());
        Assert.Equal("admin", preDeletedVars["role"].GetString());

        // 2. CORRUPT cache columns directly in database to ensure the journal is the canonical truth
        instSuspended.Status = ExecutionStatus.Pending;
        instSuspended.VariableState = null;
        verifyContext.Entry(instSuspended).Property(e => e.Status).IsModified = true;
        verifyContext.Entry(instSuspended).Property(e => e.VariableState).IsModified = true;
        await verifyContext.SaveChangesAsync();

        // 3. Read journal entries, rehydrate using JournalFoldService
        using var foldContext = new AppDbContext(_dbContextOptions);
        var entries = await foldContext.JournalEntries
            .Where(j => j.ExecutionInstanceId == instanceId)
            .OrderBy(j => j.Timestamp)
            .ToListAsync();

        var foldService = new JournalFoldService();
        var (variables, status) = foldService.FoldJournal(entries);

        // Assert reconstructed projection matches original pre-deleted state perfectly
        Assert.Equal(ExecutionStatus.Suspended, status);
        Assert.True(variables.ContainsKey("user_id"));
        Assert.Equal(42, variables["user_id"].GetInt32());
        Assert.True(variables.ContainsKey("role"));
        Assert.Equal("admin", variables["role"].GetString());
    }
}
