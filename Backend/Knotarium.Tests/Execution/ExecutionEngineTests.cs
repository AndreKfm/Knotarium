using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Knotarium.Features.Execution;
using Knotarium.Features.Nodes;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Security;
using Xunit;

namespace Knotarium.Tests.Execution;

[Collection(WorkflowExecutionIsolationCollection.Name)]
public class ExecutionEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;
    private readonly INodePackageManifestProvider _manifestProvider = new InMemoryNodePackageManifestProvider();

    public ExecutionEngineTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information)
            .EnableSensitiveDataLogging()
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

    // --- Mock Task Implementations ---

    private class FunctionalNodeTask : INodeTask
    {
        private readonly Func<NodeExecutionContext, CancellationToken, Task<LegacyNodeResult>> _func;

        public FunctionalNodeTask(Func<NodeExecutionContext, Task<LegacyNodeResult>> func)
        {
            _func = (ctx, token) => func(ctx);
        }

        public FunctionalNodeTask(Func<NodeExecutionContext, CancellationToken, Task<LegacyNodeResult>> func)
        {
            _func = func;
        }

        public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
        {
            return _func(context, cancellationToken);
        }
    }

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

    private class FakeEventPublisher : IExecutionEventPublisher
    {
        public Task PublishAsync(ExecutionInstanceId executionId, ExecutionJournal entry, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RetryAwareManifestProvider : INodePackageManifestProvider
    {
        private readonly InMemoryNodePackageManifestProvider _innerProvider = new();

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
                    new RetryPolicy(MaxAttempts: 3, InitialDelaySeconds: 2, BackoffRate: 2.0, Jitter: false, MaxDelaySeconds: 30)));
            }

            return _innerProvider.GetManifestAsync(packageId, cancellationToken);
        }
    }

    // Provides single-output ("result") manifests for the synthetic body node types used by the
    // parallelForEach container test, delegating all real node types to the in-memory provider.
    private sealed class ParallelBodyManifestProvider : INodePackageManifestProvider
    {
        private readonly InMemoryNodePackageManifestProvider _innerProvider = new();

        public Task<NodePackageManifest?> GetManifestAsync(NodePackageId packageId, CancellationToken cancellationToken = default)
        {
            if (packageId.Value.Equals("stepA", StringComparison.OrdinalIgnoreCase) ||
                packageId.Value.Equals("stepB", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<NodePackageManifest?>(new NodePackageManifest(
                    packageId,
                    "1.0.0",
                    packageId.Value,
                    "Tests",
                    NodeTier.Declarative,
                    NodeSideEffectKind.IdempotentSideEffect,
                    RecoveryMode.FailImmediately,
                    5,
                    new List<string>(),
                    new List<ParameterDefinition>(),
                    new List<OutputDefinition> { new("result") }));
            }

            return _innerProvider.GetManifestAsync(packageId, cancellationToken);
        }
    }

    // --- Tests ---

    [Fact]
    public async Task SuccessfulWorkflowExecution_OrchestratesFullyAndAppendsJournal()
    {
        // Arrange
        using var context = await CreateContextAsync();

        // 1. Define nodes & edges
        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var logNode = new NodeDefinition(NodeId.Create("log-1"), "log", new Dictionary<string, object> { ["message"] = "Static Log message" });
        var endNode = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());

        var edge1 = new EdgeDefinition("edge-1", startNode.Id, "result", logNode.Id, "in");
        var edge2 = new EdgeDefinition("edge-2", logNode.Id, "result", endNode.Id, "in");

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Simple Test Workflow",
            new[] { startNode, logNode, endNode },
            new[] { edge1, edge2 }
        );

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        // 2. Mock node tasks
        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(
            new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = "Hello from Start!" })
        )));
        registry.Register("log", new FunctionalNodeTask(ctx => {
            // Retrieve value mapped from start node
            var incoming = ctx.Inputs.TryGetValue("in", out var val) ? val.ToString() : "default";
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object> { ["loggedValue"] = incoming ?? "" }));
        }));
        registry.Register("end", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));

        // 3. Create Execution Instance
        var instanceId = ExecutionInstanceId.New();
        var instance = new ExecutionInstance
        {
            Id = instanceId,
            WorkflowDefinitionId = workflowId,
            Status = ExecutionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        context.ExecutionInstances.Add(instance);
        await context.SaveChangesAsync();

        // 4. Initialize executor
        var provider = new SqliteWorkflowDefinitionProvider(context);
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        // Act
        await executor.ExecuteAsync(instanceId);

        // Assert - Verify state is persisted
        using var readContext = new AppDbContext(_dbContextOptions);
        var retrievedInstance = await readContext.ExecutionInstances
            .Include(e => e.NodeStates)
            .Include(e => e.JournalEntries)
            .FirstOrDefaultAsync(e => e.Id == instanceId);

        Assert.NotNull(retrievedInstance);
        Assert.Equal(ExecutionStatus.Completed, retrievedInstance.Status);
        Assert.Equal(3, retrievedInstance.NodeStates.Count);

        var startState = retrievedInstance.NodeStates.First(n => n.NodeId == startNode.Id);
        var logState = retrievedInstance.NodeStates.First(n => n.NodeId == logNode.Id);
        var endState = retrievedInstance.NodeStates.First(n => n.NodeId == endNode.Id);

        Assert.Equal(NodeStatus.Completed, startState.Status);
        Assert.Equal(NodeStatus.Completed, logState.Status);
        Assert.Equal(NodeStatus.Completed, endState.Status);

        // Input propagation verification
        Assert.Equal("Hello from Start!", logState.Inputs["in"].ToString());
        Assert.Equal("Hello from Start!", logState.Outputs["loggedValue"].ToString());

        // Journal verification
        Assert.NotEmpty(retrievedInstance.JournalEntries);
        var events = retrievedInstance.JournalEntries.OrderBy(j => j.Timestamp).Select(j => j.EventType).ToList();
        Assert.Contains("WorkflowStarted", events);
        Assert.Contains("WorkflowCompleted", events);
        Assert.Equal(3, retrievedInstance.JournalEntries.Count(j => j.EventType == "NodeExecutionStarted"));
        Assert.Equal(3, retrievedInstance.JournalEntries.Count(j => j.EventType == "NodeExecutionCompleted"));
    }

    [Fact]
    public async Task ExecuteAsync_ScheduleOrigin_TraversesSchedulerEntryOnly()
    {
        using var context = await CreateContextAsync();

        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var schedulerNode = new NodeDefinition(
            NodeId.Create("scheduler-1"),
            "scheduler",
            new Dictionary<string, object>
            {
                ["cronExpression"] = "*/5 * * * *",
                ["timezoneId"] = "UTC"
            });
        var manualLogNode = new NodeDefinition(NodeId.Create("log-manual"), "log", new Dictionary<string, object> { ["message"] = "manual" });
        var scheduleLogNode = new NodeDefinition(NodeId.Create("log-schedule"), "log", new Dictionary<string, object> { ["message"] = "schedule" });

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Origin Routed Workflow",
            new[] { startNode, schedulerNode, manualLogNode, scheduleLogNode },
            new[]
            {
                new EdgeDefinition("edge-manual", startNode.Id, "result", manualLogNode.Id, "in"),
                new EdgeDefinition("edge-schedule", schedulerNode.Id, "triggeredAt", scheduleLogNode.Id, "in")
            });

        context.WorkflowDefinitions.Add(workflow);

        var instanceId = ExecutionInstanceId.New();
        context.ExecutionInstances.Add(new ExecutionInstance
        {
            Id = instanceId,
            WorkflowDefinitionId = workflowId,
            Status = ExecutionStatus.Pending,
            TriggerOrigin = "schedule",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));
        registry.Register("log", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object>
        {
            ["received"] = ctx.Inputs.TryGetValue("in", out var value) ? value ?? string.Empty : string.Empty
        }))));

        var provider = new SqliteWorkflowDefinitionProvider(context);
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        await executor.ExecuteAsync(instanceId);

        using var verificationContext = new AppDbContext(_dbContextOptions);
        var instance = await verificationContext.ExecutionInstances
            .Include(item => item.NodeStates)
            .SingleAsync(item => item.Id == instanceId);

        var schedulerState = instance.NodeStates.Single(state => state.NodeId == schedulerNode.Id);
        var scheduleLogState = instance.NodeStates.Single(state => state.NodeId == scheduleLogNode.Id);
        Assert.Equal(NodeStatus.Completed, schedulerState.Status);
        Assert.Equal(NodeStatus.Completed, scheduleLogState.Status);
        Assert.Equal(NodeStatus.Completed, schedulerState.Status);
        Assert.DoesNotContain(instance.NodeStates, state => state.NodeId == startNode.Id);
        Assert.DoesNotContain(instance.NodeStates, state => state.NodeId == manualLogNode.Id);
        Assert.True(scheduleLogState.Inputs.ContainsKey("in"));
    }

    [Fact]
    public async Task ExecuteAsync_ManualOrigin_DoesNotTraverseSchedulerEntry()
    {
        using var context = await CreateContextAsync();

        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var schedulerNode = new NodeDefinition(
            NodeId.Create("scheduler-1"),
            "scheduler",
            new Dictionary<string, object>
            {
                ["cronExpression"] = "*/5 * * * *",
                ["timezoneId"] = "UTC"
            });
        var manualLogNode = new NodeDefinition(NodeId.Create("log-manual"), "log", new Dictionary<string, object> { ["message"] = "manual" });
        var scheduleLogNode = new NodeDefinition(NodeId.Create("log-schedule"), "log", new Dictionary<string, object> { ["message"] = "schedule" });

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Origin Routed Workflow",
            new[] { startNode, schedulerNode, manualLogNode, scheduleLogNode },
            new[]
            {
                new EdgeDefinition("edge-manual", startNode.Id, "result", manualLogNode.Id, "in"),
                new EdgeDefinition("edge-schedule", schedulerNode.Id, "triggeredAt", scheduleLogNode.Id, "in")
            });

        context.WorkflowDefinitions.Add(workflow);

        var instanceId = ExecutionInstanceId.New();
        context.ExecutionInstances.Add(new ExecutionInstance
        {
            Id = instanceId,
            WorkflowDefinitionId = workflowId,
            Status = ExecutionStatus.Pending,
            TriggerOrigin = "manual",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));
        registry.Register("log", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object>
        {
            ["received"] = ctx.Inputs.TryGetValue("in", out var value) ? value ?? string.Empty : string.Empty
        }))));

        var provider = new SqliteWorkflowDefinitionProvider(context);
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        await executor.ExecuteAsync(instanceId);

        using var verificationContext = new AppDbContext(_dbContextOptions);
        var instance = await verificationContext.ExecutionInstances
            .Include(item => item.NodeStates)
            .SingleAsync(item => item.Id == instanceId);

        Assert.Contains(instance.NodeStates, state => state.NodeId == startNode.Id && state.Status == NodeStatus.Completed);
        Assert.Contains(instance.NodeStates, state => state.NodeId == manualLogNode.Id && state.Status == NodeStatus.Completed);
        Assert.DoesNotContain(instance.NodeStates, state => state.NodeId == schedulerNode.Id);
        Assert.DoesNotContain(instance.NodeStates, state => state.NodeId == scheduleLogNode.Id);
    }

    [Fact]
    public async Task ExecuteAsync_ManualOrigin_PinnedNode_ReturnsSample_WithoutRunningTask()
    {
        using var context = await CreateContextAsync();

        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var pinnedNode = new NodeDefinition(NodeId.Create("pinned-1"), "log", new Dictionary<string, object>
        {
            ["message"] = "would execute",
            ["__pinnedOutput"] = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["port"] = "result",
                ["payload"] = new Dictionary<string, object> { ["msg"] = "pinned" },
            },
        });
        var sinkNode = new NodeDefinition(NodeId.Create("sink-1"), "log", new Dictionary<string, object> { ["message"] = "downstream" });

        var workflowId = WorkflowDefinitionId.New();
        context.WorkflowDefinitions.Add(new WorkflowDefinition(
            workflowId,
            "Pinned Workflow",
            new[] { startNode, pinnedNode, sinkNode },
            new[]
            {
                new EdgeDefinition("e-start-pinned", startNode.Id, "result", pinnedNode.Id, "in"),
                new EdgeDefinition("e-pinned-sink", pinnedNode.Id, "result", sinkNode.Id, "in"),
            }));

        var instanceId = ExecutionInstanceId.New();
        context.ExecutionInstances.Add(new ExecutionInstance
        {
            Id = instanceId,
            WorkflowDefinitionId = workflowId,
            Status = ExecutionStatus.Pending,
            TriggerOrigin = "manual",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));
        registry.Register("log", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(
            new LegacyNodeResult.Success(new Dictionary<string, object> { ["received"] = "EXECUTED" }))));

        var provider = new SqliteWorkflowDefinitionProvider(context);
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        await executor.ExecuteAsync(instanceId);

        using var verificationContext = new AppDbContext(_dbContextOptions);
        var instance = await verificationContext.ExecutionInstances
            .Include(item => item.NodeStates)
            .SingleAsync(item => item.Id == instanceId);

        var pinnedState = instance.NodeStates.Single(state => state.NodeId == pinnedNode.Id);
        Assert.Equal(NodeStatus.Completed, pinnedState.Status);
        // The task was NOT run: outputs carry the pinned payload on 'result', not the task's 'received'.
        Assert.True(pinnedState.Outputs.ContainsKey("result"));
        Assert.False(pinnedState.Outputs.ContainsKey("received"));

        // The pinned value flows downstream — the sink executed and received the pin on its 'in' input.
        var sinkState = instance.NodeStates.Single(state => state.NodeId == sinkNode.Id);
        Assert.Equal(NodeStatus.Completed, sinkState.Status);
        Assert.True(sinkState.Inputs.ContainsKey("in"));
    }

    [Fact]
    public async Task ExecuteAsync_ManualOrigin_DisabledPin_RunsTaskNormally()
    {
        using var context = await CreateContextAsync();

        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var pinnedNode = new NodeDefinition(NodeId.Create("pinned-1"), "log", new Dictionary<string, object>
        {
            ["message"] = "runs",
            ["__pinnedOutput"] = new Dictionary<string, object>
            {
                ["enabled"] = false,
                ["payload"] = new Dictionary<string, object> { ["msg"] = "pinned" },
            },
        });

        var workflowId = WorkflowDefinitionId.New();
        context.WorkflowDefinitions.Add(new WorkflowDefinition(
            workflowId,
            "Disabled Pin Workflow",
            new[] { startNode, pinnedNode },
            new[] { new EdgeDefinition("e-start-pinned", startNode.Id, "result", pinnedNode.Id, "in") }));

        var instanceId = ExecutionInstanceId.New();
        context.ExecutionInstances.Add(new ExecutionInstance
        {
            Id = instanceId,
            WorkflowDefinitionId = workflowId,
            Status = ExecutionStatus.Pending,
            TriggerOrigin = "manual",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));
        registry.Register("log", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(
            new LegacyNodeResult.Success(new Dictionary<string, object> { ["received"] = "EXECUTED" }))));

        var provider = new SqliteWorkflowDefinitionProvider(context);
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        await executor.ExecuteAsync(instanceId);

        using var verificationContext = new AppDbContext(_dbContextOptions);
        var instance = await verificationContext.ExecutionInstances
            .Include(item => item.NodeStates)
            .SingleAsync(item => item.Id == instanceId);

        // A disabled pin is ignored: the task ran and produced its own output.
        var pinnedState = instance.NodeStates.Single(state => state.NodeId == pinnedNode.Id);
        Assert.Equal(NodeStatus.Completed, pinnedState.Status);
        Assert.True(pinnedState.Outputs.ContainsKey("received"));
    }

    [Fact]
    public async Task ExecuteAsync_DeviceEventOrigin_StartsAtTheExplicitEntryNode()
    {
        using var context = await CreateContextAsync();

        // A device-block event pin wired to a Log: the device node is not an entry, the run is seeded
        // directly at the downstream Log via the explicit entry-node ids the enqueuer carried.
        var deviceNode = new NodeDefinition(NodeId.Create("device-1"), "externalDevice", new Dictionary<string, object>
        {
            ["targetId"] = new Dictionary<string, object> { ["value"] = "siteA" },
        });
        var logNode = new NodeDefinition(NodeId.Create("log-1"), "log", new Dictionary<string, object> { ["message"] = "fired" });

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Device Event Workflow",
            new[] { deviceNode, logNode },
            new[] { new EdgeDefinition("edge-evt", deviceNode.Id, "evt:1:started", logNode.Id, "in") });

        context.WorkflowDefinitions.Add(workflow);

        var instanceId = ExecutionInstanceId.New();
        context.ExecutionInstances.Add(new ExecutionInstance
        {
            Id = instanceId,
            WorkflowDefinitionId = workflowId,
            Status = ExecutionStatus.Pending,
            TriggerOrigin = Knotarium.Features.Execution.ExternalSignalRunEnqueuer.DeviceEventTriggerOrigin,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            GlobalVariables = new Dictionary<string, object>
            {
                [Knotarium.Features.Execution.ExternalSignalRunEnqueuer.EntryNodesVariableKey] = new List<string> { "log-1" }
            }
        });
        await context.SaveChangesAsync();

        var registry = new MockNodeTaskRegistry();
        registry.Register("log", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));

        var provider = new SqliteWorkflowDefinitionProvider(context);
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        await executor.ExecuteAsync(instanceId);

        using var verificationContext = new AppDbContext(_dbContextOptions);
        var instance = await verificationContext.ExecutionInstances
            .Include(item => item.NodeStates)
            .SingleAsync(item => item.Id == instanceId);

        Assert.Contains(instance.NodeStates, state => state.NodeId == logNode.Id && state.Status == NodeStatus.Completed);
        Assert.DoesNotContain(instance.NodeStates, state => state.NodeId == deviceNode.Id);
        Assert.Equal(ExecutionStatus.Completed, instance.Status);
    }

    [Fact]
    public async Task BranchingConditionNode_ExecutesOnlyActiveBranch()
    {
        // Arrange
        using var context = await CreateContextAsync();

        // Nodes & Edges
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var conditionNode = new NodeDefinition(NodeId.Create("cond"), "condition", new Dictionary<string, object>());
        var leftNode = new NodeDefinition(NodeId.Create("left"), "log", new Dictionary<string, object>());
        var rightNode = new NodeDefinition(NodeId.Create("right"), "log", new Dictionary<string, object>());

        var edgeStart = new EdgeDefinition("e-start", startNode.Id, "result", conditionNode.Id, "in");
        var edgeLeft = new EdgeDefinition("e-left", conditionNode.Id, "true", leftNode.Id, "in");
        var edgeRight = new EdgeDefinition("e-right", conditionNode.Id, "false", rightNode.Id, "in");

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Branching Test Workflow",
            new[] { startNode, conditionNode, leftNode, rightNode },
            new[] { edgeStart, edgeLeft, edgeRight }
        );

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        // Mock tasks
        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(
            new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = true })
        )));
        
        registry.Register("condition", new FunctionalNodeTask(ctx => {
            var inputVal = ctx.Inputs.TryGetValue("in", out var val) && (bool)val;
            string selectedPort = inputVal ? "true" : "false";
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object>
            {
                ["selectedPort"] = selectedPort
            }));
        }));

        registry.Register("log", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));

        // Instance 1: Should Go Left (True)
        var instanceId1 = ExecutionInstanceId.New();
        context.ExecutionInstances.Add(new ExecutionInstance
        {
            Id = instanceId1,
            WorkflowDefinitionId = workflowId,
            Status = ExecutionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        var provider = new SqliteWorkflowDefinitionProvider(context);
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        // Act 1
        await executor.ExecuteAsync(instanceId1);

        // Assert 1
        using var readContext1 = new AppDbContext(_dbContextOptions);
        var inst1 = await readContext1.ExecutionInstances.Include(e => e.NodeStates).FirstAsync(e => e.Id == instanceId1);
        Assert.Equal(ExecutionStatus.Completed, inst1.Status);
        Assert.Contains(inst1.NodeStates, ns => ns.NodeId.Value == "left" && ns.Status == NodeStatus.Completed);
        Assert.DoesNotContain(inst1.NodeStates, ns => ns.NodeId.Value == "right");

        // Instance 2: Should Go Right (False)
        // We override the start task to return false
        registry.Register("start", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(
            new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = false })
        )));

        var instanceId2 = ExecutionInstanceId.New();
        using var context2 = new AppDbContext(_dbContextOptions);
        context2.ExecutionInstances.Add(new ExecutionInstance
        {
            Id = instanceId2,
            WorkflowDefinitionId = workflowId,
            Status = ExecutionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await context2.SaveChangesAsync();

        var writer2 = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor2 = new WorkflowExecutor(context2, compiler, registry, new FakeEventPublisher(), writer2);

        // Act 2
        await executor2.ExecuteAsync(instanceId2);

        // Assert 2
        using var readContext2 = new AppDbContext(_dbContextOptions);
        var inst2 = await readContext2.ExecutionInstances.Include(e => e.NodeStates).FirstAsync(e => e.Id == instanceId2);
        Assert.Equal(ExecutionStatus.Completed, inst2.Status);
        Assert.Contains(inst2.NodeStates, ns => ns.NodeId.Value == "right" && ns.Status == NodeStatus.Completed);
        Assert.DoesNotContain(inst2.NodeStates, ns => ns.NodeId.Value == "left");
    }

    [Fact]
    public async Task LongRunningSuspendAndResume_CorrectlyPausesAndResumesWorkflow()
    {
        // Arrange
        using var context = await CreateContextAsync();

        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var delayNode = new NodeDefinition(NodeId.Create("delay"), "delay", new Dictionary<string, object>());
        var endNode = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());

        var edge1 = new EdgeDefinition("e1", startNode.Id, "result", delayNode.Id, "in");
        var edge2 = new EdgeDefinition("e2", delayNode.Id, "result", endNode.Id, "in");

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Suspend Resume Workflow",
            new[] { startNode, delayNode, endNode },
            new[] { edge1, edge2 }
        );

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));
        registry.Register("delay", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.WaitForEvent("webhook_received"))));
        registry.Register("end", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));

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
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        // Act - Step 1: Initial execution to trigger suspend
        await executor.ExecuteAsync(instanceId);

        // Assert - Verify suspended state
        using var readContext1 = new AppDbContext(_dbContextOptions);
        var instSuspended = await readContext1.ExecutionInstances.Include(e => e.NodeStates).Include(e => e.JournalEntries).FirstAsync(e => e.Id == instanceId);
        
        Assert.Equal(ExecutionStatus.Suspended, instSuspended.Status);
        Assert.Equal(2, instSuspended.NodeStates.Count);
        
        var delayState = instSuspended.NodeStates.First(n => n.NodeId == delayNode.Id);
        Assert.Equal(NodeStatus.Waiting, delayState.Status);
        Assert.Equal("webhook_received", delayState.Outputs["eventName"].ToString());
        Assert.DoesNotContain(instSuspended.NodeStates, n => n.NodeId == endNode.Id);
        Assert.Contains(instSuspended.JournalEntries, j => j.EventType == "WorkflowSuspended");

        var contextResume = new AppDbContext(_dbContextOptions);
        var writerResume = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var resumeExecutor = new WorkflowExecutor(contextResume, compiler, registry, new FakeEventPublisher(), writerResume);
        var eventData = new Dictionary<string, object> { { "payload", "ok-123" }, { "result", "ok-123" } };
        
        await resumeExecutor.ExecuteAsync(instanceId, "webhook_received", eventData);

        // Assert - Verify completed state and value propagation
        using var readContext2 = new AppDbContext(_dbContextOptions);
        var instCompleted = await readContext2.ExecutionInstances.Include(e => e.NodeStates).Include(e => e.JournalEntries).FirstAsync(e => e.Id == instanceId);

        Assert.Equal(ExecutionStatus.Completed, instCompleted.Status);
        Assert.Equal(3, instCompleted.NodeStates.Count);

        var finalDelayState = instCompleted.NodeStates.First(n => n.NodeId == delayNode.Id);
        Assert.Equal(NodeStatus.Completed, finalDelayState.Status);
        Assert.Equal("ok-123", finalDelayState.Outputs["payload"].ToString());

        var finalEndState = instCompleted.NodeStates.First(n => n.NodeId == endNode.Id);
        Assert.Equal(NodeStatus.Completed, finalEndState.Status);
        Assert.Equal("ok-123", finalEndState.Inputs["in"].ToString()); // Value mapped from resumed node's output!
        Assert.Contains(instCompleted.JournalEntries, j => j.EventType == "NodeResumed");
        Assert.Contains(instCompleted.JournalEntries, j => j.EventType == "WorkflowCompleted");
    }

    [Fact]
    public async Task ProcessWorkItemAsync_ResumeWorkItem_CompletesDownstreamWithoutReexecutingCompletedNodes()
    {
        using var context = await CreateContextAsync();

        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var waitNode = new NodeDefinition(NodeId.Create("wait"), "delay", new Dictionary<string, object>());
        var endNode = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());

        var edges = new[]
        {
            new EdgeDefinition("e1", startNode.Id, "result", waitNode.Id, "in"),
            new EdgeDefinition("e2", waitNode.Id, "result", endNode.Id, "in")
        };

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(workflowId, "Work Item Resume", new[] { startNode, waitNode, endNode }, edges);
        var workflowVersion = new WorkflowVersion(WorkflowVersionId.New(), workflowId, 1, workflow.Nodes, workflow.Edges, DateTimeOffset.UtcNow);
        context.WorkflowDefinitions.Add(workflow);
        context.WorkflowVersions.Add(workflowVersion);

        var startExecutions = 0;
        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(ctx =>
        {
            startExecutions++;
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = "started" }));
        }));
        registry.Register("delay", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.WaitForEvent("resume"))));
        registry.Register("end", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object> { ["received"] = ctx.Inputs["in"] }))));

        var instanceId = ExecutionInstanceId.New();
        context.ExecutionInstances.Add(new ExecutionInstance
        {
            Id = instanceId,
            WorkflowDefinitionId = workflowId,
            WorkflowVersionId = workflowVersion.Id,
            Status = ExecutionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        var provider = new SqliteWorkflowDefinitionProvider(context);
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        await executor.ExecuteAsync(instanceId);

        var workItemId = Guid.NewGuid();
        var resumePayload = JsonSerializer.Serialize(new
        {
            nodeId = waitNode.Id.Value,
            workflowVersionId = workflowVersion.Id.Value,
            output = "ok-123"
        });

        context.JournalEntries.Add(new ExecutionJournal
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = instanceId,
            NodeId = waitNode.Id,
            Timestamp = DateTimeOffset.UtcNow,
            EventType = JournalEventTypes.WorkflowResumed,
            Message = "Resume registered.",
            Data = new Dictionary<string, object> { ["Output"] = "ok-123" }
        });
        context.ExecutionWorkItems.Add(new ExecutionWorkItem
        {
            Id = workItemId,
            ExecutionInstanceId = instanceId,
            Type = "Resume",
            Payload = resumePayload,
            Status = WorkItemStatus.Running,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        await executor.ProcessWorkItemAsync(workItemId);

        using var verificationContext = new AppDbContext(_dbContextOptions);
        var execution = await verificationContext.ExecutionInstances
            .Include(item => item.NodeStates)
            .Include(item => item.JournalEntries)
            .SingleAsync(item => item.Id == instanceId);
        var workItem = await verificationContext.ExecutionWorkItems.SingleAsync(item => item.Id == workItemId);

        Assert.Equal(1, startExecutions);
        Assert.Equal(ExecutionStatus.Completed, execution.Status);
        Assert.Equal(WorkItemStatus.Completed, workItem.Status);
        Assert.NotNull(workItem.ProcessedAtUtc);

        var persistedStartNode = execution.NodeStates.Single(state => state.NodeId == startNode.Id);
        var persistedWaitNode = execution.NodeStates.Single(state => state.NodeId == waitNode.Id);
        var persistedEndNode = execution.NodeStates.Single(state => state.NodeId == endNode.Id);

        Assert.Equal(1, persistedStartNode.ExecutionCount);
        Assert.Equal(NodeStatus.Completed, persistedWaitNode.Status);
        Assert.Equal("ok-123", persistedWaitNode.Outputs["result"].ToString());
        Assert.Equal(NodeStatus.Completed, persistedEndNode.Status);
        Assert.Equal("ok-123", persistedEndNode.Inputs["in"].ToString());
        Assert.Contains(execution.JournalEntries, entry => entry.EventType == "NodeResumed");
        Assert.Contains(execution.JournalEntries, entry => entry.EventType == JournalEventTypes.WorkflowCompleted);
    }

    [Fact]
    public async Task WorkflowExecutionWorker_PollsPendingWorkItems_WhenQueueIsIdle()
    {
        var databasePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"knotarium-worker-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var workerDbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            await using var seedConnection = new SqliteConnection(connectionString);
            await seedConnection.OpenAsync();

            var seedDbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(seedConnection)
                .Options;

            await using var seedContext = new AppDbContext(seedDbContextOptions);
            await seedContext.Database.EnsureCreatedAsync();

            var startNode = new NodeDefinition(NodeId.Create("start-worker"), "start", new Dictionary<string, object>());
            var waitNode = new NodeDefinition(NodeId.Create("wait-worker"), "delay", new Dictionary<string, object>());
            var endNode = new NodeDefinition(NodeId.Create("end-worker"), "end", new Dictionary<string, object>());
            var workflowId = WorkflowDefinitionId.New();
            var workflow = new WorkflowDefinition(
                workflowId,
                "Worker Work Item Resume",
                new[] { startNode, waitNode, endNode },
                new[]
                {
                    new EdgeDefinition("w1", startNode.Id, "result", waitNode.Id, "in"),
                    new EdgeDefinition("w2", waitNode.Id, "result", endNode.Id, "in")
                });
            var workflowVersion = new WorkflowVersion(WorkflowVersionId.New(), workflowId, 1, workflow.Nodes, workflow.Edges, DateTimeOffset.UtcNow);

            seedContext.WorkflowDefinitions.Add(workflow);
            seedContext.WorkflowVersions.Add(workflowVersion);

            var instanceId = ExecutionInstanceId.New();
            seedContext.ExecutionInstances.Add(new ExecutionInstance
            {
                Id = instanceId,
                WorkflowDefinitionId = workflowId,
                WorkflowVersionId = workflowVersion.Id,
                Status = ExecutionStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await seedContext.SaveChangesAsync();

            var registry = new MockNodeTaskRegistry();
            registry.Register("start", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = "started" }))));
            registry.Register("delay", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.WaitForEvent("resume"))));
            registry.Register("end", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object> { ["done"] = true }))));

            var seedProvider = new SqliteWorkflowDefinitionProvider(seedContext);
            var seedCompiler = new WorkflowCompiler(seedProvider, _manifestProvider);
            var seedWriter = new SqliteExecutionJournalWriter(connectionString, seedConnection);
            var seedExecutor = new WorkflowExecutor(seedContext, seedCompiler, registry, new FakeEventPublisher(), seedWriter);
            await seedExecutor.ExecuteAsync(instanceId);

            var workItemId = Guid.NewGuid();
            seedContext.JournalEntries.Add(new ExecutionJournal
            {
                Id = Guid.NewGuid(),
                ExecutionInstanceId = instanceId,
                NodeId = waitNode.Id,
                Timestamp = DateTimeOffset.UtcNow,
                EventType = JournalEventTypes.WorkflowResumed,
                Message = "Resume registered.",
                Data = new Dictionary<string, object> { ["Output"] = "worker-ok" }
            });
            seedContext.ExecutionWorkItems.Add(new ExecutionWorkItem
            {
                Id = workItemId,
                ExecutionInstanceId = instanceId,
                Type = "Resume",
                Payload = JsonSerializer.Serialize(new
                {
                    nodeId = waitNode.Id.Value,
                    workflowVersionId = workflowVersion.Id.Value,
                    output = "worker-ok"
                }),
                Status = WorkItemStatus.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await seedContext.SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddSingleton(_manifestProvider);
            services.AddSingleton(registry);
            services.AddSingleton<INodeTaskRegistry>(sp => sp.GetRequiredService<MockNodeTaskRegistry>());
            services.AddSingleton<IExecutionEventPublisher, FakeEventPublisher>();
            services.AddSingleton<ExecutionTelemetry>();
            services.AddSingleton<ICorrelationTokenCrypto, CorrelationTokenCrypto>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<WorkflowExecutionQueue>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
            services.AddScoped<DatabaseWorkflowStore>();
            services.AddScoped<IWorkflowStore>(sp => sp.GetRequiredService<DatabaseWorkflowStore>());
            services.AddScoped<IWorkflowDefinitionProvider>(sp => sp.GetRequiredService<DatabaseWorkflowStore>());
            services.AddScoped<WorkflowCompiler>();
            services.AddScoped<IExecutionJournalWriter>(_ => new SqliteExecutionJournalWriter(connectionString));
            services.AddScoped<WorkflowExecutor>();
            services.AddScoped<RecoveryService>();

            using var serviceProvider = services.BuildServiceProvider();
            var queue = serviceProvider.GetRequiredService<WorkflowExecutionQueue>();
            var worker = new WorkflowExecutionWorker(queue, serviceProvider, NullLogger<WorkflowExecutionWorker>.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StartAsync(cts.Token);

            ExecutionStatus? finalStatus = null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(150, cts.Token);

                await using var verificationContext = new AppDbContext(workerDbContextOptions);
                var execution = await verificationContext.ExecutionInstances.SingleAsync(item => item.Id == instanceId, cts.Token);
                if (execution.Status == ExecutionStatus.Completed)
                {
                    finalStatus = execution.Status;
                    break;
                }
            }

            await worker.StopAsync(CancellationToken.None);

            await using var finalContext = new AppDbContext(workerDbContextOptions);
            var finalExecution = await finalContext.ExecutionInstances
                .Include(item => item.NodeStates)
                .SingleAsync(item => item.Id == instanceId);
            var finalWorkItem = await finalContext.ExecutionWorkItems.SingleAsync(item => item.Id == workItemId);

            Assert.Equal(ExecutionStatus.Completed, finalStatus);
            Assert.Equal(ExecutionStatus.Completed, finalExecution.Status);
            Assert.Equal(WorkItemStatus.Completed, finalWorkItem.Status);
            Assert.Contains(finalExecution.NodeStates, state => state.NodeId == endNode.Id && state.Status == NodeStatus.Completed);
        }
        finally
        {
            if (System.IO.File.Exists(databasePath))
            {
                try
                {
                    System.IO.File.Delete(databasePath);
                }
                catch (System.IO.IOException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task IdempotencyAndFailureHandling_EnsuresCompletedNodesDoNotReExecuteAndFailsSafely()
    {
        // Arrange
        using var context = await CreateContextAsync();

        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var faultyNode = new NodeDefinition(NodeId.Create("faulty"), "httpRequest", new Dictionary<string, object>());
        var endNode = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());

        var edge1 = new EdgeDefinition("e1", startNode.Id, "result", faultyNode.Id, "in");
        var edge2 = new EdgeDefinition("e2", faultyNode.Id, "success", endNode.Id, "in");

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Failure Test Workflow",
            new[] { startNode, faultyNode, endNode },
            new[] { edge1, edge2 }
        );

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        int startExecCount = 0;

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(ctx => {
            startExecCount++;
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success());
        }));
        
        // This task will fail
        registry.Register("httpRequest", new FunctionalNodeTask(ctx => 
            Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Failure("Network Connection Failed"))));
            
        registry.Register("end", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));

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
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        // Act - Run 1: Should fail at faulty node
        await executor.ExecuteAsync(instanceId);

        // Assert - Verify failure
        using var readContext1 = new AppDbContext(_dbContextOptions);
        var instFailed = await readContext1.ExecutionInstances.Include(e => e.NodeStates).Include(e => e.JournalEntries).FirstAsync(e => e.Id == instanceId);

        Assert.Equal(ExecutionStatus.Failed, instFailed.Status);
        
        var startState = instFailed.NodeStates.First(n => n.NodeId == startNode.Id);
        Assert.Equal(NodeStatus.Completed, startState.Status);
        Assert.Equal(1, startState.ExecutionCount);
        Assert.Equal(1, startExecCount);

        var faultyState = instFailed.NodeStates.First(n => n.NodeId == faultyNode.Id);
        Assert.Equal(NodeStatus.Failed, faultyState.Status);
        Assert.Equal("Network Connection Failed", faultyState.ErrorMessage);
        Assert.Equal(1, faultyState.ExecutionCount);

        Assert.DoesNotContain(instFailed.NodeStates, n => n.NodeId == endNode.Id);
        Assert.Contains(instFailed.JournalEntries, j => j.EventType == "WorkflowFailed");

        // Act - Run 2: Re-run the same instance (idempotency check)
        // Reset status to Pending to trigger retry
        instFailed.Status = ExecutionStatus.Pending;
        await readContext1.SaveChangesAsync();

        var contextRetry = new AppDbContext(_dbContextOptions);
        var writerRetry = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var retryExecutor = new WorkflowExecutor(contextRetry, compiler, registry, new FakeEventPublisher(), writerRetry);
        await retryExecutor.ExecuteAsync(instanceId);

        // Assert - Verify start task did not execute again
        using var readContext2 = new AppDbContext(_dbContextOptions);
        var instRetried = await readContext2.ExecutionInstances.Include(e => e.NodeStates).FirstAsync(e => e.Id == instanceId);
        
        var retriedStartState = instRetried.NodeStates.First(n => n.NodeId == startNode.Id);
        Assert.Equal(1, retriedStartState.ExecutionCount); // Count remains 1!
        Assert.Equal(1, startExecCount); // Mock invocation count remains 1!
    }

    [Fact]
    public async Task WorkflowExecutionWorker_StartupGuard_EnforcesExactlyOneActiveWorker()
    {
        // Arrange
        using var context = await CreateContextAsync();
        
        var services = new ServiceCollection();
        services.AddSingleton(new WorkflowExecutionQueue());
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped<RecoveryService>();
        
        var serviceProvider = services.BuildServiceProvider();
        
        var logger1 = Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowExecutionWorker>.Instance;
        var logger2 = Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowExecutionWorker>.Instance;
        
        var queue = serviceProvider.GetRequiredService<WorkflowExecutionQueue>();
        
        var worker1 = new WorkflowExecutionWorker(queue, serviceProvider, logger1);
        var worker2 = new WorkflowExecutionWorker(queue, serviceProvider, logger2);
        
        using var cts = new CancellationTokenSource();
        
        // Start worker 1 - should succeed
        await worker1.StartAsync(cts.Token);
        
        // Let the worker run its startup guard in the background
        await Task.Delay(200, cts.Token);
        
        // Start worker 2 - StartAsync returns immediately as it starts ExecuteAsync in background
        await worker2.StartAsync(cts.Token);

        // Let the worker run its startup guard in the background
        await Task.Delay(200, cts.Token);
        
        // The background task ExecuteTask should throw InvalidOperationException due to the active worker lock
        Assert.NotNull(worker2.ExecuteTask);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => worker2.ExecuteTask);
        Assert.Contains("Another active executor worker is already running", exception.Message);
        
        // Stop worker 1 gracefully
        await worker1.StopAsync(cts.Token);
        
        // Now worker 3 should start successfully because the lock was cleaned up!
        var worker3 = new WorkflowExecutionWorker(queue, serviceProvider, logger2);
        await worker3.StartAsync(cts.Token);
        await worker3.StopAsync(cts.Token);
    }

    [Fact]
    public async Task ExecutionEngine_NodeTimeout_FailsNodeAndWritesJournalCorrectly()
    {
        // Arrange
        using var context = await CreateContextAsync();
        
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var hangingNode = new NodeDefinition(NodeId.Create("hang"), "delay", new Dictionary<string, object> 
        { 
            ["timeoutSeconds"] = 1 // 1 second timeout
        });
        var endNode = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());
        
        var edge1 = new EdgeDefinition("e1", startNode.Id, "result", hangingNode.Id, "in");
        var edge2 = new EdgeDefinition("e2", hangingNode.Id, "result", endNode.Id, "in");
        
        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Timeout Test Workflow",
            new[] { startNode, hangingNode, endNode },
            new[] { edge1, edge2 }
        );
        
        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();
        
        bool wasCancelled = false;
        
        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));
        registry.Register("delay", new FunctionalNodeTask(async (ctx, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new LegacyNodeResult.Success();
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
                throw;
            }
        }));
        
        registry.Register("end", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));
        
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
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);
        
        // Act
        await executor.ExecuteAsync(instanceId);
        
        // Assert
        using var readContext = new AppDbContext(_dbContextOptions);
        var inst = await readContext.ExecutionInstances
            .Include(e => e.NodeStates)
            .Include(e => e.JournalEntries)
            .FirstAsync(e => e.Id == instanceId);
            
        Assert.Equal(ExecutionStatus.Failed, inst.Status);
        
        var hangingState = inst.NodeStates.First(n => n.NodeId == hangingNode.Id);
        Assert.Equal(NodeStatus.Failed, hangingState.Status);
        Assert.Contains("timed out after 1s", hangingState.ErrorMessage);
        
        // Verify node received cancellation
        Assert.True(wasCancelled);
        
        // Verify journal entry recorded
        Assert.Contains(inst.JournalEntries, j => j.EventType == "NodeExecutionFailed" && j.Message.Contains("timed out after 1 seconds"));
    }

    [Fact]
    public async Task ExecutionEngine_NodeFailureWithErrorCode_WritesCodeAsDiscreteJournalField()
    {
        // R6: a structured code on the failure is recorded as its OWN field in the (hash-chained) audit
        // journal Data, so an auditor can field-filter on it rather than substring-matching the message.
        var data = await RunFailingNodeAndReadFailureDataAsync(
            new LegacyNodeResult.Failure(
                "[RESOLUTION_FAILED] Operand 'a' could not be resolved (comparator 'c1', operand 'a')",
                "RESOLUTION_FAILED"));

        Assert.Equal("RESOLUTION_FAILED", ReadJournalString(data, "errorCode"));
    }

    [Fact]
    public async Task ExecutionEngine_NodeFailureWithoutCode_OmitsErrorCodeJournalField()
    {
        // A plain failure (no structured code) must not invent an errorCode key — absence is meaningful.
        var data = await RunFailingNodeAndReadFailureDataAsync(
            new LegacyNodeResult.Failure("plain failure with no structured code"));

        Assert.False(data.ContainsKey("errorCode"));
    }

    private async Task<IReadOnlyDictionary<string, object>> RunFailingNodeAndReadFailureDataAsync(LegacyNodeResult failure)
    {
        using var context = await CreateContextAsync();

        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var boomNode = new NodeDefinition(NodeId.Create("boom"), "log", new Dictionary<string, object>());
        var edge = new EdgeDefinition("e1", startNode.Id, "result", boomNode.Id, "in");

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(workflowId, "Failure Code Workflow",
            new[] { startNode, boomNode }, new[] { edge });
        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));
        registry.Register("log", new FunctionalNodeTask(ctx => Task.FromResult(failure)));

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
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        await executor.ExecuteAsync(instanceId);

        using var readContext = new AppDbContext(_dbContextOptions);
        var inst = await readContext.ExecutionInstances
            .Include(e => e.JournalEntries)
            .FirstAsync(e => e.Id == instanceId);

        var failedEntry = inst.JournalEntries.First(j => j.EventType == "NodeExecutionFailed");
        return failedEntry.Data;
    }

    private static string? ReadJournalString(IReadOnlyDictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var v) || v is null) return null;
        return v is JsonElement je ? je.GetString() : v.ToString();
    }

    [Fact]
    public async Task ExecuteAsync_RetryableFailure_SchedulesRetryStateAndWorkItem()
    {
        using var context = await CreateContextAsync();

        var manifestProvider = new RetryAwareManifestProvider();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var retryNode = new NodeDefinition(NodeId.Create("retry"), "retryable", new Dictionary<string, object>());
        var endNode = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());
        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Retry scheduling workflow",
            new[] { startNode, retryNode, endNode },
            new[]
            {
                new EdgeDefinition("r1", startNode.Id, "result", retryNode.Id, "in"),
                new EdgeDefinition("r2", retryNode.Id, "success", endNode.Id, "in")
            });

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));
        registry.Register("retryable", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Failure("Authorization: Bearer secret_key_123"))));
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

        using var verificationContext = new AppDbContext(_dbContextOptions);
        var instance = await verificationContext.ExecutionInstances
            .Include(item => item.NodeStates)
            .SingleAsync(item => item.Id == instanceId);
        var retryState = await verificationContext.NodeRetryStates.SingleAsync(item => item.ExecutionInstanceId == instanceId && item.NodeId == retryNode.Id);
        var workItem = await verificationContext.ExecutionWorkItems.SingleAsync(item => item.ExecutionInstanceId == instanceId);

        Assert.Equal(ExecutionStatus.WaitingForRetry, instance.Status);
        Assert.Equal(2, retryState.AttemptNumber);
        Assert.Equal(WorkItemStatus.Pending, workItem.Status);
        Assert.Equal("Retry", workItem.Type);
        Assert.Equal(retryState.NextRetryAtUtc, workItem.NotBeforeUtc);
        Assert.Contains("retry", workItem.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2", workItem.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret_key_123", retryState.SanitizedFailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessWorkItemAsync_RetryWorkItem_ReexecutesNodeAndClearsRetryState()
    {
        using var context = await CreateContextAsync();

        var manifestProvider = new RetryAwareManifestProvider();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var retryNode = new NodeDefinition(NodeId.Create("retry"), "retryable", new Dictionary<string, object>());
        var endNode = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());
        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Retry execution workflow",
            new[] { startNode, retryNode, endNode },
            new[]
            {
                new EdgeDefinition("r1", startNode.Id, "result", retryNode.Id, "in"),
                new EdgeDefinition("r2", retryNode.Id, "success", endNode.Id, "in")
            });

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        var retryExecutions = 0;
        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(_ => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));
        registry.Register("retryable", new FunctionalNodeTask(_ =>
        {
            retryExecutions++;
            return Task.FromResult<LegacyNodeResult>(retryExecutions == 1
                ? new LegacyNodeResult.Failure("temporary outage")
                : new LegacyNodeResult.Success(new Dictionary<string, object> { ["success"] = "retried" }));
        }));
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

        var pendingRetryId = await context.ExecutionWorkItems
            .Where(item => item.ExecutionInstanceId == instanceId)
            .Select(item => item.Id)
            .SingleAsync();

        await executor.ProcessWorkItemAsync(pendingRetryId);

        using var verificationContext = new AppDbContext(_dbContextOptions);
        var instance = await verificationContext.ExecutionInstances
            .Include(item => item.NodeStates)
            .Include(item => item.JournalEntries)
            .SingleAsync(item => item.Id == instanceId);
        var workItem = await verificationContext.ExecutionWorkItems.SingleAsync(item => item.Id == pendingRetryId);

        Assert.Equal(2, retryExecutions);
        Assert.Equal(ExecutionStatus.Completed, instance.Status);
        Assert.Equal(WorkItemStatus.Completed, workItem.Status);
        Assert.Empty(await verificationContext.NodeRetryStates.Where(item => item.ExecutionInstanceId == instanceId).ToListAsync());
        Assert.Equal(2, instance.NodeStates.Single(item => item.NodeId == retryNode.Id).ExecutionCount);
        Assert.Contains(instance.JournalEntries, entry => entry.EventType == "NodeRetryStarted");
        Assert.Contains(instance.JournalEntries, entry => entry.EventType == JournalEventTypes.WorkflowCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_VariableReferenceProperty_ResolvesAndPassesContent()
    {
        // Arrange
        using var context = await CreateContextAsync();

        var startNode = new NodeDefinition(NodeId.Create("start-node-id"), "start", new Dictionary<string, object>());
        var logNode = new NodeDefinition(NodeId.Create("log-node-id"), "log", new Dictionary<string, object>
        {
            ["message"] = new Dictionary<string, object>
            {
                ["__type"] = "variable_ref",
                ["variableId"] = "var-123",
                ["variableName"] = "start-node-id_body"
            }
        });

        var edge = new EdgeDefinition("e1", startNode.Id, "result", logNode.Id, "in");

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Var Ref Resolve Workflow",
            new[] { startNode, logNode },
            new[] { edge }
        );

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(
            new LegacyNodeResult.Success(new Dictionary<string, object> { ["body"] = "test output payload" })
        )));
        
        object? loggedMessage = null;
        registry.Register("log", new FunctionalNodeTask(ctx => {
            ctx.Inputs.TryGetValue("message", out loggedMessage);
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success());
        }));

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
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        // Act
        await executor.ExecuteAsync(instanceId);

        // Assert
        using var readContext = new AppDbContext(_dbContextOptions);
        var inst = await readContext.ExecutionInstances
            .Include(e => e.NodeStates)
            .FirstAsync(e => e.Id == instanceId);

        Assert.Equal(ExecutionStatus.Completed, inst.Status);
        Assert.Equal("test output payload", loggedMessage?.ToString());

        var logNodeState = inst.NodeStates.First(n => n.NodeId == logNode.Id);
        Assert.Equal("test output payload", logNodeState.Inputs["message"]?.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_ForLoopWithCount_ExecutesBodyMultipleTimes()
    {
        // Arrange
        using var context = await CreateContextAsync();

        var startNode = new NodeDefinition(NodeId.Create("start-node"), "start", new Dictionary<string, object>());
        var loopNode = new NodeDefinition(NodeId.Create("loop-node"), "forLoop", new Dictionary<string, object>
        {
            ["mode"] = "count",
            ["count"] = 10
        });
        var bodyNode = new NodeDefinition(NodeId.Create("body-node"), "log", new Dictionary<string, object>
        {
            ["message"] = "body run"
        });
        var endNode = new NodeDefinition(NodeId.Create("end-node"), "end", new Dictionary<string, object>());

        var edge1 = new EdgeDefinition("e1", startNode.Id, "result", loopNode.Id, "count");
        var edge2 = new EdgeDefinition("e2", loopNode.Id, "start", bodyNode.Id, "message");
        var edge3 = new EdgeDefinition("e3", bodyNode.Id, "result", loopNode.Id, "end");
        var edge4 = new EdgeDefinition("e4", loopNode.Id, "success", endNode.Id, "payload");

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "For Loop Integration Test",
            new[] { startNode, loopNode, bodyNode, endNode },
            new[] { edge1, edge2, edge3, edge4 }
        );

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(
            new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = 10 })
        )));
        
        // Register the actual ForLoopNodeTask
        registry.Register("forLoop", new ForLoopNodeTask((InMemoryNodePackageManifestProvider)_manifestProvider));

        int bodyExecutionCount = 0;
        registry.Register("log", new FunctionalNodeTask(ctx => {
            bodyExecutionCount++;
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = $"result-{bodyExecutionCount}" }));
        }));

        registry.Register("end", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));

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
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        // Act
        await executor.ExecuteAsync(instanceId);

        // Assert
        using var readContext = new AppDbContext(_dbContextOptions);
        var inst = await readContext.ExecutionInstances
            .Include(e => e.NodeStates)
            .FirstAsync(e => e.Id == instanceId);

        Assert.Equal(ExecutionStatus.Completed, inst.Status);
        Assert.Equal(10, bodyExecutionCount);
    }

    [Fact]
    public async Task ExecuteAsync_ParallelForEach_RunsBodyConcurrentlyAndAggregatesResults()
    {
        // Arrange: a 2-node body subgraph (stepA -> stepB) run once per item, concurrently.
        //   start -> parallelForEach -[start]-> stepA -> stepB -[end loopback]-> parallelForEach
        //   parallelForEach -[success]-> end
        // Each item flows item -> stepA ("a:item") -> stepB ("a:item|b"), and stepB feeds the loop's
        // 'end' input, so each per-item result must be "a:{item}|b".
        using var context = await CreateContextAsync();

        var startNode = new NodeDefinition(NodeId.Create("start-node"), "start", new Dictionary<string, object>());
        var pforNode = new NodeDefinition(NodeId.Create("pfor-node"), "parallelForEach", new Dictionary<string, object>
        {
            ["maxParallelism"] = 3
        });
        var stepA = new NodeDefinition(NodeId.Create("step-a"), "stepA", new Dictionary<string, object>());
        var stepB = new NodeDefinition(NodeId.Create("step-b"), "stepB", new Dictionary<string, object>());
        var endNode = new NodeDefinition(NodeId.Create("end-node"), "end", new Dictionary<string, object>());

        var edges = new[]
        {
            new EdgeDefinition("e1", startNode.Id, "result", pforNode.Id, "collection"),
            new EdgeDefinition("e2", pforNode.Id, "start", stepA.Id, "in"),
            new EdgeDefinition("e3", stepA.Id, "result", stepB.Id, "in"),
            new EdgeDefinition("e4", stepB.Id, "result", pforNode.Id, "end"),
            new EdgeDefinition("e5", pforNode.Id, "success", endNode.Id, "payload")
        };

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Parallel For-Each Container Test",
            new[] { startNode, pforNode, stepA, stepB, endNode },
            edges);

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        // stepA / stepB need manifests so compilation accepts them; reuse the registered manifests
        // by aliasing onto a provider that maps the test types to simple single-output shapes.
        var manifestProvider = new ParallelBodyManifestProvider();

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(
            new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = "[1,2,3,4,5,6]" }))));

        var concurrencyLock = new object();
        var current = 0;
        var maxObserved = 0;
        var total = 0;
        void Enter()
        {
            lock (concurrencyLock)
            {
                current++;
                total++;
                if (current > maxObserved) maxObserved = current;
            }
        }
        void Exit()
        {
            lock (concurrencyLock) { current--; }
        }

        registry.Register("stepA", new FunctionalNodeTask(async ctx =>
        {
            Enter();
            await Task.Delay(30);
            Exit();
            var item = ctx.Inputs.TryGetValue("item", out var v) ? v : null;
            return new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = $"a:{item}" });
        }));
        registry.Register("stepB", new FunctionalNodeTask(async ctx =>
        {
            Enter();
            await Task.Delay(30);
            Exit();
            var inValue = ctx.Inputs.TryGetValue("in", out var v) ? v : null;
            return new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = $"{inValue}|b" });
        }));
        registry.Register("end", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));

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

        // Act
        await executor.ExecuteAsync(instanceId);

        // Assert
        using var readContext = new AppDbContext(_dbContextOptions);
        var inst = await readContext.ExecutionInstances
            .Include(e => e.NodeStates)
            .FirstAsync(e => e.Id == instanceId);

        Assert.Equal(ExecutionStatus.Completed, inst.Status);
        Assert.Equal(12, total); // 6 items x 2 body nodes
        Assert.True(maxObserved > 1, $"expected concurrent iterations, observed max {maxObserved}");
        Assert.True(maxObserved <= 3, $"maxParallelism=3 exceeded, observed {maxObserved}");

        var pforState = inst.NodeStates.First(n => n.NodeId == pforNode.Id);
        Assert.Equal(NodeStatus.Completed, pforState.Status);
        var results = (JsonElement)pforState.Outputs["results"];
        Assert.Equal(JsonValueKind.Array, results.ValueKind);
        Assert.Equal(6, results.GetArrayLength());

        // Each per-item result is the value stepB fed back into the loop's 'end' input.
        var resultValues = results.EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("a:1|b", resultValues);
        Assert.Contains("a:6|b", resultValues);
    }

    [Fact]
    public async Task ExecuteAsync_Join_WaitsForAllBranchesBeforeRunning()
    {
        // Arrange: a short branch (branchA) and a longer branch (b1 -> b2) converge on a join.
        // The join is first *reached* while b2 is still pending; the wait-for-all gate must defer it
        // until both branches complete, so the join must aggregate exactly 2 branch results.
        using var context = await CreateContextAsync();

        var startNode = new NodeDefinition(NodeId.Create("start-node"), "start", new Dictionary<string, object>());
        var branchA = new NodeDefinition(NodeId.Create("branch-a"), "log", new Dictionary<string, object>());
        var b1 = new NodeDefinition(NodeId.Create("branch-b1"), "log", new Dictionary<string, object>());
        var b2 = new NodeDefinition(NodeId.Create("branch-b2"), "log", new Dictionary<string, object>());
        var joinNode = new NodeDefinition(NodeId.Create("join-node"), "join", new Dictionary<string, object>());
        var endNode = new NodeDefinition(NodeId.Create("end-node"), "end", new Dictionary<string, object>());

        var edges = new[]
        {
            new EdgeDefinition("e1", startNode.Id, "result", branchA.Id, "in"),
            new EdgeDefinition("e2", startNode.Id, "result", b1.Id, "in"),
            new EdgeDefinition("e3", b1.Id, "result", b2.Id, "in"),
            new EdgeDefinition("e4", branchA.Id, "result", joinNode.Id, "in"),
            new EdgeDefinition("e5", b2.Id, "result", joinNode.Id, "in"),
            new EdgeDefinition("e6", joinNode.Id, "result", endNode.Id, "payload")
        };

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Join Wait-For-All Test",
            new[] { startNode, branchA, b1, b2, joinNode, endNode },
            edges);

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(
            new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = "go" }))));
        registry.Register("log", new FunctionalNodeTask(ctx =>
        {
            var id = ctx.NodeId.Value;
            return Task.FromResult<LegacyNodeResult>(
                new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = $"r-{id}" }));
        }));
        registry.Register("join", new JoinNodeTask());
        registry.Register("end", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));

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
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);

        // Act
        await executor.ExecuteAsync(instanceId);

        // Assert
        using var readContext = new AppDbContext(_dbContextOptions);
        var inst = await readContext.ExecutionInstances
            .Include(e => e.NodeStates)
            .FirstAsync(e => e.Id == instanceId);

        Assert.Equal(ExecutionStatus.Completed, inst.Status);

        var joinState = inst.NodeStates.First(n => n.NodeId == joinNode.Id);
        Assert.Equal(NodeStatus.Completed, joinState.Status);
        Assert.Equal(1, joinState.ExecutionCount); // ran exactly once, not once per branch

        var results = (JsonElement)joinState.Outputs["results"];
        Assert.Equal(JsonValueKind.Array, results.ValueKind);
        Assert.Equal(2, results.GetArrayLength()); // both branches aggregated -> gate held until both done
    }

    [Fact]
    public async Task ExecuteAsync_ParallelForEach_FansOutFromStartAndFansInToEnd()
    {
        // A body with TWO branches: start -> A -> end AND start -> B -> end. Both branches must run
        // for every item, and both must feed the loop's 'end' input, so each per-item result is the
        // 2-element list [A-result, B-result].
        using var context = await CreateContextAsync();

        var startNode = new NodeDefinition(NodeId.Create("start-node"), "start", new Dictionary<string, object>());
        var pforNode = new NodeDefinition(NodeId.Create("pfor-node"), "parallelForEach", new Dictionary<string, object>());
        var branchA = new NodeDefinition(NodeId.Create("branch-a"), "stepA", new Dictionary<string, object>());
        var branchB = new NodeDefinition(NodeId.Create("branch-b"), "stepB", new Dictionary<string, object>());
        var endNode = new NodeDefinition(NodeId.Create("end-node"), "end", new Dictionary<string, object>());

        var edges = new[]
        {
            new EdgeDefinition("e1", startNode.Id, "result", pforNode.Id, "collection"),
            new EdgeDefinition("e2", pforNode.Id, "start", branchA.Id, "in"),   // start fan-out #1
            new EdgeDefinition("e3", pforNode.Id, "start", branchB.Id, "in"),   // start fan-out #2
            new EdgeDefinition("e4", branchA.Id, "result", pforNode.Id, "end"), // end fan-in #1
            new EdgeDefinition("e5", branchB.Id, "result", pforNode.Id, "end"), // end fan-in #2
            new EdgeDefinition("e6", pforNode.Id, "success", endNode.Id, "payload")
        };

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Parallel For-Each Fan-out/Fan-in Test",
            new[] { startNode, pforNode, branchA, branchB, endNode },
            edges);

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        var manifestProvider = new ParallelBodyManifestProvider();

        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(
            new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = "[1,2,3]" }))));
        registry.Register("stepA", new FunctionalNodeTask(ctx =>
        {
            var item = ctx.Inputs.TryGetValue("item", out var v) ? v : null;
            return Task.FromResult<LegacyNodeResult>(
                new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = $"A{item}" }));
        }));
        registry.Register("stepB", new FunctionalNodeTask(ctx =>
        {
            var item = ctx.Inputs.TryGetValue("item", out var v) ? v : null;
            return Task.FromResult<LegacyNodeResult>(
                new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = $"B{item}" }));
        }));
        registry.Register("end", new FunctionalNodeTask(ctx => Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));

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

        // Act
        await executor.ExecuteAsync(instanceId);

        // Assert
        using var readContext = new AppDbContext(_dbContextOptions);
        var inst = await readContext.ExecutionInstances
            .Include(e => e.NodeStates)
            .FirstAsync(e => e.Id == instanceId);

        Assert.Equal(ExecutionStatus.Completed, inst.Status);

        var pforState = inst.NodeStates.First(n => n.NodeId == pforNode.Id);
        var results = (JsonElement)pforState.Outputs["results"];
        Assert.Equal(3, results.GetArrayLength()); // one entry per item

        // Each item's entry is the 2-branch fan-in list [A{item}, B{item}].
        var firstItem = results[0].EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("A1", firstItem);
        Assert.Contains("B1", firstItem);
    }
}

