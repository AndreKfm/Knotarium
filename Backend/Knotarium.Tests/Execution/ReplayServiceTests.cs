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

[Collection(WorkflowExecutionIsolationCollection.Name)]
public class ReplayServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;
    private readonly InMemoryNodePackageManifestProvider _manifestProvider = new();

    public ReplayServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
    }

    public void Dispose() => _connection.Dispose();

    private async Task<AppDbContext> CreateContextAsync()
    {
        var context = new AppDbContext(_dbContextOptions);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private sealed class FunctionalNodeTask : INodeTask
    {
        private readonly Func<NodeExecutionContext, Task<LegacyNodeResult>> _func;
        public FunctionalNodeTask(Func<NodeExecutionContext, Task<LegacyNodeResult>> func) => _func = func;
        public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) => _func(context);
    }

    private sealed class MockNodeTaskRegistry : INodeTaskRegistry
    {
        private readonly Dictionary<string, INodeTask> _registry = new(StringComparer.OrdinalIgnoreCase);
        public void Register(string type, INodeTask task) => _registry[type] = task;
        public INodeTask? GetTask(string nodeType) => _registry.TryGetValue(nodeType, out var task) ? task : null;
    }

    private sealed class FakeEventPublisher : IExecutionEventPublisher
    {
        public Task PublishAsync(ExecutionInstanceId executionId, ExecutionJournal entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    // Shared scenario:  start -> setvar -> reader -> tail
    //   setvar writes GlobalVariables["x"] = "hello"
    //   reader reads GlobalVariables["x"] (the variable) and "in" (its predecessor's output)
    private sealed record Scenario(
        WorkflowExecutor Executor,
        ReplayService ReplayService,
        AppDbContext Context,
        WorkflowDefinitionId WorkflowId,
        WorkflowVersionId VersionId,
        Counters Counters);

    private sealed class Counters
    {
        public int Start;
        public int SetVar;
        public int Reader;
        public int Tail;
    }

    private async Task<Scenario> BuildScenarioAsync(AppDbContext context)
    {
        var start = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var setvar = new NodeDefinition(NodeId.Create("setvar"), "setVariable", new Dictionary<string, object>());
        var reader = new NodeDefinition(NodeId.Create("reader"), "log", new Dictionary<string, object>());
        var tail = new NodeDefinition(NodeId.Create("tail"), "end", new Dictionary<string, object>());

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(workflowId, "Replay scenario", new[] { start, setvar, reader, tail }, new[]
        {
            new EdgeDefinition("e1", start.Id, "result", setvar.Id, "in"),
            new EdgeDefinition("e2", setvar.Id, "result", reader.Id, "in"),
            new EdgeDefinition("e3", reader.Id, "result", tail.Id, "payload")
        });
        var version = new WorkflowVersion(WorkflowVersionId.New(), workflowId, 1, workflow.Nodes, workflow.Edges, DateTimeOffset.UtcNow);

        context.WorkflowDefinitions.Add(workflow);
        context.WorkflowVersions.Add(version);
        await context.SaveChangesAsync();

        var counters = new Counters();
        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(_ =>
        {
            counters.Start++;
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = "go" }));
        }));
        registry.Register("setVariable", new FunctionalNodeTask(ctx =>
        {
            counters.SetVar++;
            ctx.GlobalVariables["x"] = "hello";
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = "setvar-out" }));
        }));
        registry.Register("log", new FunctionalNodeTask(ctx =>
        {
            counters.Reader++;
            var seenVar = ctx.GlobalVariables.TryGetValue("x", out var v) ? v?.ToString() : null;
            var fromPrev = ctx.Inputs.TryGetValue("in", out var i) ? i?.ToString() : null;
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object>
            {
                ["result"] = $"{seenVar}|{fromPrev}"
            }));
        }));
        registry.Register("end", new FunctionalNodeTask(ctx =>
        {
            counters.Tail++;
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object>
            {
                ["payload"] = ctx.Inputs.TryGetValue("payload", out var p) ? p! : ""
            }));
        }));

        var provider = new SqliteWorkflowDefinitionProvider(context);
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);
        var replayService = new ReplayService(context, compiler);

        return new Scenario(executor, replayService, context, workflowId, version.Id, counters);
    }

    private async Task<ExecutionInstanceId> RunSourceAsync(Scenario s)
    {
        var instanceId = ExecutionInstanceId.New();
        s.Context.ExecutionInstances.Add(new ExecutionInstance
        {
            Id = instanceId,
            WorkflowDefinitionId = s.WorkflowId,
            WorkflowVersionId = s.VersionId,
            Status = ExecutionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await s.Context.SaveChangesAsync();
        await s.Executor.ExecuteAsync(instanceId);
        return instanceId;
    }

    [Fact]
    public async Task Step1_VariablesBefore_CapturesSnapshotPerNode()
    {
        using var context = await CreateContextAsync();
        var s = await BuildScenarioAsync(context);
        var sourceId = await RunSourceAsync(s);

        using var verify = new AppDbContext(_dbContextOptions);
        var states = await verify.NodeStates.Where(n => n.ExecutionInstanceId == sourceId).ToListAsync();

        var setvarBefore = Deserialize(states.Single(n => n.NodeId == NodeId.Create("setvar")).VariablesBefore);
        var readerBefore = Deserialize(states.Single(n => n.NodeId == NodeId.Create("reader")).VariablesBefore);

        // setvar runs BEFORE x is assigned (the snapshot is taken at node start).
        Assert.False(setvarBefore.ContainsKey("x"));
        // reader runs AFTER setvar assigned x.
        Assert.True(readerBefore.ContainsKey("x"));
        Assert.Equal("hello", readerBefore["x"].ToString());
    }

    [Fact]
    public async Task Step4_CreateReplay_SeedsUpstream_ResetsDownstream_SetsLineage()
    {
        using var context = await CreateContextAsync();
        var s = await BuildScenarioAsync(context);
        var sourceId = await RunSourceAsync(s);

        var result = await s.ReplayService.CreateReplayAsync(sourceId, NodeId.Create("reader"));

        Assert.NotNull(result);

        using var verify = new AppDbContext(_dbContextOptions);
        var replay = await verify.ExecutionInstances
            .Include(e => e.NodeStates)
            .SingleAsync(e => e.Id == result!.NewExecutionId);

        Assert.Equal("replay", replay.TriggerOrigin);
        Assert.Equal(sourceId, replay.ReplayOfExecutionId);
        Assert.Equal(NodeId.Create("reader"), replay.ReplayFromNodeId);

        // Seeded upstream: start + setvar present as Completed with original outputs.
        var seededNodeIds = replay.NodeStates.Select(n => n.NodeId.Value).OrderBy(v => v).ToArray();
        Assert.Equal(new[] { "setvar", "start" }, seededNodeIds);
        Assert.Equal("setvar-out", replay.NodeStates.Single(n => n.NodeId == NodeId.Create("setvar")).Outputs["result"].ToString());

        // Cut-point variable state restored from reader.VariablesBefore.
        Assert.Equal("hello", replay.GlobalVariables["x"].ToString());

        // Exactly one pending Replay work item.
        var workItems = await verify.ExecutionWorkItems.Where(w => w.ExecutionInstanceId == result!.NewExecutionId).ToListAsync();
        var workItem = Assert.Single(workItems);
        Assert.Equal("Replay", workItem.Type);
        Assert.Equal(WorkItemStatus.Pending, workItem.Status);
    }

    [Fact]
    public async Task Step5_ReplayWorkItem_ReexecutesDownstream_KeepsSeedsAndVariables()
    {
        using var context = await CreateContextAsync();
        var s = await BuildScenarioAsync(context);
        var sourceId = await RunSourceAsync(s);

        // Source run executed each node exactly once.
        Assert.Equal((1, 1, 1, 1), (s.Counters.Start, s.Counters.SetVar, s.Counters.Reader, s.Counters.Tail));

        var result = await s.ReplayService.CreateReplayAsync(sourceId, NodeId.Create("reader"));
        var workItemId = await s.Context.ExecutionWorkItems
            .Where(w => w.ExecutionInstanceId == result!.NewExecutionId)
            .Select(w => w.Id)
            .SingleAsync();

        await s.Executor.ProcessWorkItemAsync(workItemId);

        // Upstream seeds were NOT re-invoked; cut-point + downstream WERE.
        Assert.Equal(1, s.Counters.Start);
        Assert.Equal(1, s.Counters.SetVar);
        Assert.Equal(2, s.Counters.Reader);
        Assert.Equal(2, s.Counters.Tail);

        using var verify = new AppDbContext(_dbContextOptions);
        var replay = await verify.ExecutionInstances
            .Include(e => e.NodeStates)
            .SingleAsync(e => e.Id == result!.NewExecutionId);

        Assert.Equal(ExecutionStatus.Completed, replay.Status);

        // reader saw the restored variable (hello) and the seeded predecessor output (setvar-out).
        var readerOut = replay.NodeStates.Single(n => n.NodeId == NodeId.Create("reader")).Outputs["result"].ToString();
        Assert.Equal("hello|setvar-out", readerOut);

        // Fresh re-execution in the replay instance.
        Assert.Equal(1, replay.NodeStates.Single(n => n.NodeId == NodeId.Create("reader")).ExecutionCount);

        // Source run is untouched.
        var source = await verify.ExecutionInstances.Include(e => e.NodeStates).SingleAsync(e => e.Id == sourceId);
        Assert.Equal(ExecutionStatus.Completed, source.Status);
        Assert.Equal(1, source.NodeStates.Single(n => n.NodeId == NodeId.Create("reader")).ExecutionCount);
    }

    [Theory]
    [InlineData(true, 1, "real-call-1")]   // mock on: task NOT invoked again, original output forwarded
    [InlineData(false, 2, "real-call-2")]  // mock off: task invoked for real
    public async Task Step10_MockSideEffects_ControlsWhetherNonIdempotentNodeFires(
        bool mockSideEffects,
        int expectedTotalCalls,
        string expectedReplayOutput)
    {
        using var context = await CreateContextAsync();

        var start = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var http = new NodeDefinition(NodeId.Create("http"), "httpRequest", new Dictionary<string, object>());
        var tail = new NodeDefinition(NodeId.Create("tail"), "end", new Dictionary<string, object>());

        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(workflowId, "Mock scenario", new[] { start, http, tail }, new[]
        {
            new EdgeDefinition("e1", start.Id, "result", http.Id, "in"),
            new EdgeDefinition("e2", http.Id, "success", tail.Id, "payload")
        });
        var version = new WorkflowVersion(WorkflowVersionId.New(), workflowId, 1, workflow.Nodes, workflow.Edges, DateTimeOffset.UtcNow);
        context.WorkflowDefinitions.Add(workflow);
        context.WorkflowVersions.Add(version);
        await context.SaveChangesAsync();

        var httpCalls = 0;
        var registry = new MockNodeTaskRegistry();
        registry.Register("start", new FunctionalNodeTask(_ =>
            Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = "go" }))));
        registry.Register("httpRequest", new FunctionalNodeTask(_ =>
        {
            httpCalls++;
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object> { ["success"] = $"real-call-{httpCalls}" }));
        }));
        registry.Register("end", new FunctionalNodeTask(_ =>
            Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success())));

        var provider = new SqliteWorkflowDefinitionProvider(context);
        var compiler = new WorkflowCompiler(provider, _manifestProvider);
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);
        var executor = new WorkflowExecutor(context, compiler, registry, new FakeEventPublisher(), writer);
        var replayService = new ReplayService(context, compiler);

        var sourceId = ExecutionInstanceId.New();
        context.ExecutionInstances.Add(new ExecutionInstance
        {
            Id = sourceId,
            WorkflowDefinitionId = workflowId,
            WorkflowVersionId = version.Id,
            Status = ExecutionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        await executor.ExecuteAsync(sourceId);

        Assert.Equal(1, httpCalls); // source run fired the real effect once

        var result = await replayService.CreateReplayAsync(sourceId, NodeId.Create("http"), mockSideEffects: mockSideEffects);
        var workItemId = await context.ExecutionWorkItems
            .Where(w => w.ExecutionInstanceId == result!.NewExecutionId)
            .Select(w => w.Id)
            .SingleAsync();

        await executor.ProcessWorkItemAsync(workItemId);

        Assert.Equal(expectedTotalCalls, httpCalls);

        using var verify = new AppDbContext(_dbContextOptions);
        var replay = await verify.ExecutionInstances
            .Include(e => e.NodeStates)
            .SingleAsync(e => e.Id == result!.NewExecutionId);

        Assert.Equal(ExecutionStatus.Completed, replay.Status);
        var httpOutput = replay.NodeStates.Single(n => n.NodeId == NodeId.Create("http")).Outputs["success"].ToString();
        Assert.Equal(expectedReplayOutput, httpOutput);
    }

    private static Dictionary<string, object> Deserialize(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, object>()
            : JsonSerializer.Deserialize<Dictionary<string, object>>(json, PersistenceJsonOptions.Default) ?? new();
}
