using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Compiler;
using KnotGarden.Features.Execution;
using KnotGarden.Infrastructure.Persistence;
using Xunit;

namespace KnotGarden.Tests.Execution;

public class RetryPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;

    public RetryPersistenceTests()
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

    [Fact]
    public async Task SaveChanges_DuplicateExecutionInstanceAndNodeId_ThrowsUniqueConstraintViolation()
    {
        await using var context = await CreateContextAsync();
        var executionInstanceId = ExecutionInstanceId.New();
        var nodeId = NodeId.Create("retry-node");

        context.NodeRetryStates.Add(new NodeRetryState
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = executionInstanceId,
            NodeId = nodeId,
            AttemptNumber = 2,
            NextRetryAtUtc = DateTimeOffset.UtcNow.AddSeconds(5),
            SanitizedFailureMessage = "first"
        });

        await context.SaveChangesAsync();

        context.NodeRetryStates.Add(new NodeRetryState
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = executionInstanceId,
            NodeId = nodeId,
            AttemptNumber = 3,
            NextRetryAtUtc = DateTimeOffset.UtcNow.AddSeconds(10),
            SanitizedFailureMessage = "second"
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("UNIQUE", exception.InnerException?.Message ?? exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessWorkItemAsync_FinalRetryFailure_ClearsRetryStateAndFailsWorkflow()
    {
        await using var context = await CreateContextAsync();

        var manifestProvider = new RetryManifestProvider(new RetryPolicy(MaxAttempts: 2, InitialDelaySeconds: 1, BackoffRate: 1.0, Jitter: false, MaxDelaySeconds: 5));
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var retryNode = new NodeDefinition(NodeId.Create("retry"), "retryable", new Dictionary<string, object>());
        var endNode = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());

        context.WorkflowDefinitions.Add(new WorkflowDefinition(
            workflowId,
            "Retry persistence final failure",
            new[] { startNode, retryNode, endNode },
            new[]
            {
                new EdgeDefinition("e1", startNode.Id, "result", retryNode.Id, "in"),
                new EdgeDefinition("e2", retryNode.Id, "success", endNode.Id, "in")
            }));
        await context.SaveChangesAsync();

        var registry = new TestNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));
        registry.Register("retryable", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Failure("Authorization: Bearer final_secret"))));
        registry.Register("end", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));

        var instanceId = ExecutionInstanceId.New();
        context.ExecutionInstances.Add(new ExecutionInstance
        {
            Id = instanceId,
            WorkflowDefinitionId = workflowId,
            Status = ExecutionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        var provider = new SqliteWorkflowDefinitionProvider(context);
        var compiler = new WorkflowCompiler(provider, manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        await executor.ExecuteAsync(instanceId);

        var workItemId = await context.ExecutionWorkItems
            .Where(item => item.ExecutionInstanceId == instanceId)
            .Select(item => item.Id)
            .SingleAsync();

        await executor.ProcessWorkItemAsync(workItemId);

        await using var verificationContext = new AppDbContext(_dbContextOptions);
        var instance = await verificationContext.ExecutionInstances
            .Include(item => item.NodeStates)
            .Include(item => item.JournalEntries)
            .SingleAsync(item => item.Id == instanceId);

        Assert.Equal(ExecutionStatus.Failed, instance.Status);
        Assert.Empty(await verificationContext.NodeRetryStates.Where(item => item.ExecutionInstanceId == instanceId).ToListAsync());
        Assert.Contains(instance.JournalEntries, entry => entry.EventType == JournalEventTypes.NodeExecutionFailed);
        Assert.Contains(instance.JournalEntries, entry => entry.EventType == "WorkflowFailed");
    }

    private async Task<AppDbContext> CreateContextAsync()
    {
        var context = new AppDbContext(_dbContextOptions);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private sealed class FunctionalNodeTask : INodeTask
    {
        private readonly Func<NodeExecutionContext, CancellationToken, Task<LegacyNodeResult>> _func;

        public FunctionalNodeTask(Func<NodeExecutionContext, Task<LegacyNodeResult>> func)
        {
            _func = (context, _) => func(context);
        }

        public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
        {
            return _func(context, cancellationToken);
        }
    }

    private sealed class TestNodeTaskRegistry : INodeTaskRegistry
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

    private sealed class FakeEventPublisher : IExecutionEventPublisher
    {
        public Task PublishAsync(ExecutionInstanceId executionId, ExecutionJournal entry, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RetryManifestProvider : INodePackageManifestProvider
    {
        private readonly InMemoryNodePackageManifestProvider _innerProvider = new();
        private readonly RetryPolicy _retryPolicy;

        public RetryManifestProvider(RetryPolicy retryPolicy)
        {
            _retryPolicy = retryPolicy;
        }

        public Task<NodePackageManifest?> GetManifestAsync(NodePackageId packageId, CancellationToken cancellationToken = default)
        {
            if (packageId.Value.Equals("retryable", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<NodePackageManifest?>(new NodePackageManifest(
                    new NodePackageId("retryable"),
                    "1.0.0",
                    "Retryable",
                    "Tests",
                    NodeTier.Declarative,
                    NodeSideEffectKind.IdempotentSideEffect,
                    RecoveryMode.RetryAutomatically,
                    5,
                    new List<string>(),
                    new List<ParameterDefinition>(),
                    new List<OutputDefinition> { new("success") },
                    _retryPolicy));
            }

            return _innerProvider.GetManifestAsync(packageId, cancellationToken);
        }
    }
}