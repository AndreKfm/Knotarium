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

[Collection(WorkflowExecutionIsolationCollection.Name)]
public class CrashRecoveryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;

    public CrashRecoveryTests()
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
    public async Task ExecuteAsync_NonIdempotentFailure_WritesMatchingAttemptIdAndRecoveryDoesNotFlagManualDecision()
    {
        await using var context = await CreateContextAsync();

        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var httpNode = new NodeDefinition(NodeId.Create("http"), "httpRequest", new Dictionary<string, object>());

        context.WorkflowDefinitions.Add(new WorkflowDefinition(
            workflowId,
            "Crash recovery clean failure",
            new[] { startNode, httpNode },
            new[] { new EdgeDefinition("e1", startNode.Id, "result", httpNode.Id, "in") }));
        await context.SaveChangesAsync();

        var registry = new TestNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));
        registry.Register("httpRequest", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Failure("boom"))));

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

        var compiler = new WorkflowCompiler(new SqliteWorkflowDefinitionProvider(context), new InMemoryNodePackageManifestProvider());
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        await executor.ExecuteAsync(instanceId);

        await using var recoveryContext = new AppDbContext(_dbContextOptions);
        var recoveryService = new RecoveryService(recoveryContext);
        var recovered = await recoveryService.RecoverIncompleteExternalEffectsAsync();

        var instance = await recoveryContext.ExecutionInstances
            .Include(item => item.NodeStates)
            .Include(item => item.JournalEntries)
            .SingleAsync(item => item.Id == instanceId);

        var marker = instance.JournalEntries.Single(entry => entry.EventType == JournalEventTypes.AttemptingExternalEffect);
        var failure = instance.JournalEntries.Last(entry => entry.EventType == JournalEventTypes.NodeExecutionFailed);

        Assert.Equal(0, recovered);
        Assert.Equal(marker.Data["AttemptId"].ToString(), failure.Data["AttemptId"].ToString());
        Assert.DoesNotContain(instance.NodeStates, state => state.Status == NodeStatus.RequiresManualDecision);
    }

    [Fact]
    public async Task RecoverIncompleteExternalEffectsAsync_UnfinishedAttempt_SuspendsExecutionAndRequiresManualDecision()
    {
        await using var context = await CreateContextAsync();

        var instanceId = ExecutionInstanceId.New();
        var nodeId = NodeId.Create("http");
        var attemptId = Guid.NewGuid().ToString();

        context.ExecutionInstances.Add(new ExecutionInstance
        {
            Id = instanceId,
            WorkflowDefinitionId = WorkflowDefinitionId.New(),
            Status = ExecutionStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            NodeStates = new List<NodeState>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ExecutionInstanceId = instanceId,
                    NodeId = nodeId,
                    Status = NodeStatus.Running,
                    ExecutionCount = 1
                }
            },
            JournalEntries = new List<ExecutionJournal>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ExecutionInstanceId = instanceId,
                    NodeId = nodeId,
                    Timestamp = DateTimeOffset.UtcNow,
                    EventType = JournalEventTypes.AttemptingExternalEffect,
                    Message = "Attempting external effect.",
                    Data = new Dictionary<string, object>
                    {
                        ["NodeId"] = nodeId.Value,
                        ["AttemptId"] = attemptId,
                        ["SideEffectKind"] = NodeSideEffectKind.NonIdempotentSideEffect.ToString()
                    }
                }
            }
        });
        await context.SaveChangesAsync();

        var recoveryService = new RecoveryService(context);
        var recovered = await recoveryService.RecoverIncompleteExternalEffectsAsync();

        await using var verificationContext = new AppDbContext(_dbContextOptions);
        var instance = await verificationContext.ExecutionInstances
            .Include(item => item.NodeStates)
            .Include(item => item.JournalEntries)
            .SingleAsync(item => item.Id == instanceId);

        Assert.Equal(1, recovered);
        Assert.Equal(ExecutionStatus.Suspended, instance.Status);
        Assert.Equal(NodeStatus.RequiresManualDecision, instance.NodeStates.Single().Status);
        Assert.Contains(instance.JournalEntries, entry => entry.EventType == JournalEventTypes.ManualDecisionRecorded);
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
}