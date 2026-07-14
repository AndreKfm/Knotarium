using System;
using System.Collections.Generic;
using System.Linq;
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
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Security;
using Xunit;

namespace Knotarium.Tests.Execution;

/// <summary>
/// Pins the run-level concurrency semantics of <see cref="WorkflowExecutionWorker"/>:
/// <c>MaxConcurrentRuns = 1</c> reproduces the historical fully-serial behavior (runs never overlap),
/// while higher values let independent runs make progress concurrently, bounded by the slot count.
/// Overlap is proven with gate-based blocking nodes (runs signal arrival and wait for release), not
/// wall-clock timing, so the assertions are deterministic.
/// </summary>
[Collection(WorkflowExecutionIsolationCollection.Name)]
public class ParallelExecutionWorkerTests
{
    private sealed class FunctionalNodeTask : INodeTask
    {
        private readonly Func<NodeExecutionContext, CancellationToken, Task<LegacyNodeResult>> _func;

        public FunctionalNodeTask(Func<NodeExecutionContext, CancellationToken, Task<LegacyNodeResult>> func)
        {
            _func = func;
        }

        public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
        {
            return _func(context, cancellationToken);
        }
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
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>Shared state of the gate-based "block" node the concurrency tests observe.</summary>
    private sealed class BlockGate
    {
        private int _current;
        private int _maxObserved;
        private int _arrivals;

        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Arrivals => Volatile.Read(ref _arrivals);
        public int MaxObservedConcurrency => Volatile.Read(ref _maxObserved);

        public async Task<LegacyNodeResult> EnterAsync()
        {
            var current = Interlocked.Increment(ref _current);
            int snapshot;
            while (current > (snapshot = Volatile.Read(ref _maxObserved)))
            {
                Interlocked.CompareExchange(ref _maxObserved, current, snapshot);
            }

            Interlocked.Increment(ref _arrivals);

            // Hold the run inside the node until the test releases it (bounded so a broken
            // implementation fails the test instead of hanging the suite).
            await Task.WhenAny(Release.Task, Task.Delay(TimeSpan.FromSeconds(15)));

            Interlocked.Decrement(ref _current);
            return new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = "ok" });
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public string DatabasePath = string.Empty;
        public string ConnectionString = string.Empty;
        public List<ExecutionInstanceId> RunIds = new();
        public ServiceProvider ServiceProvider = null!;
        public WorkflowExecutionWorker Worker = null!;
        public BlockGate Gate = null!;
        private BatchingExecutionJournalWriter? _batchingWriter;

        public static async Task<Harness> CreateAsync(int runCount, ExecutionOptions options, bool useBatchingWriter = false)
        {
            var harness = new Harness
            {
                DatabasePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"knotarium-parallel-{Guid.NewGuid():N}.db"),
                Gate = new BlockGate(),
            };
            harness.ConnectionString = $"Data Source={harness.DatabasePath}";

            // Seed: one workflow (start → block) and runCount Pending run instances. The worker's own
            // startup recovery re-queues Pending runs, so nothing needs to be enqueued manually.
            var seedOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(harness.ConnectionString).Options;
            await using (var seedContext = new AppDbContext(seedOptions))
            {
                await seedContext.Database.EnsureCreatedAsync();

                // "log" is a registered declarative node with a "result" port; we bind a blocking task
                // to it in the registry below so the run parks inside the node until the gate is released.
                var startNode = new NodeDefinition(NodeId.Create("start-p"), "start", new Dictionary<string, object>());
                var blockNode = new NodeDefinition(NodeId.Create("block-p"), "log", new Dictionary<string, object>());
                var workflowId = WorkflowDefinitionId.New();
                var workflow = new WorkflowDefinition(
                    workflowId,
                    "Parallel Execution Probe",
                    new[] { startNode, blockNode },
                    new[] { new EdgeDefinition("e1", startNode.Id, "result", blockNode.Id, "in") });
                var version = new WorkflowVersion(WorkflowVersionId.New(), workflowId, 1, workflow.Nodes, workflow.Edges, DateTimeOffset.UtcNow);

                seedContext.WorkflowDefinitions.Add(workflow);
                seedContext.WorkflowVersions.Add(version);

                for (var i = 0; i < runCount; i++)
                {
                    var runId = ExecutionInstanceId.New();
                    harness.RunIds.Add(runId);
                    seedContext.ExecutionInstances.Add(new ExecutionInstance
                    {
                        Id = runId,
                        WorkflowDefinitionId = workflowId,
                        WorkflowVersionId = version.Id,
                        Status = ExecutionStatus.Pending,
                        CreatedAt = DateTimeOffset.UtcNow.AddMilliseconds(i), // stable FIFO recovery order
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
                }

                await seedContext.SaveChangesAsync();
            }

            var registry = new MockNodeTaskRegistry();
            registry.Register("start", new FunctionalNodeTask((_, _) =>
                Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = "started" }))));
            registry.Register("log", new FunctionalNodeTask(async (_, _) => await harness.Gate.EnterAsync()));

            var services = new ServiceCollection();
            services.AddSingleton<INodePackageManifestProvider>(new InMemoryNodePackageManifestProvider());
            services.AddSingleton<INodeTaskRegistry>(registry);
            services.AddSingleton<IExecutionEventPublisher, FakeEventPublisher>();
            services.AddSingleton<ExecutionTelemetry>();
            services.AddSingleton<ICorrelationTokenCrypto, CorrelationTokenCrypto>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(new WorkflowExecutionQueue(options));
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(harness.ConnectionString));
            services.AddScoped<DatabaseWorkflowStore>();
            services.AddScoped<IWorkflowStore>(sp => sp.GetRequiredService<DatabaseWorkflowStore>());
            services.AddScoped<IWorkflowDefinitionProvider>(sp => sp.GetRequiredService<DatabaseWorkflowStore>());
            services.AddScoped<WorkflowCompiler>();
            if (useBatchingWriter)
            {
                harness._batchingWriter = new BatchingExecutionJournalWriter(
                    new SqliteExecutionJournalWriter(harness.ConnectionString), options);
                services.AddSingleton<IExecutionJournalWriter>(harness._batchingWriter);
            }
            else
            {
                services.AddScoped<IExecutionJournalWriter>(_ => new SqliteExecutionJournalWriter(harness.ConnectionString));
            }
            services.AddScoped<WorkflowExecutor>();
            services.AddScoped<RecoveryService>();

            harness.ServiceProvider = services.BuildServiceProvider();
            harness.Worker = new WorkflowExecutionWorker(
                harness.ServiceProvider.GetRequiredService<WorkflowExecutionQueue>(),
                harness.ServiceProvider,
                NullLogger<WorkflowExecutionWorker>.Instance,
                options);

            return harness;
        }

        public async Task<List<ExecutionStatus>> GetRunStatusesAsync()
        {
            var contextOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ConnectionString).Options;
            await using var context = new AppDbContext(contextOptions);
            var byId = await context.ExecutionInstances.ToDictionaryAsync(e => e.Id, e => e.Status);
            return RunIds.Select(id => byId[id]).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            Gate.Release.TrySetResult();
            await Worker.StopAsync(CancellationToken.None);
            if (_batchingWriter != null)
            {
                await _batchingWriter.DisposeAsync();
            }
            ServiceProvider.Dispose();

            SqliteConnection.ClearAllPools();
            try
            {
                if (System.IO.File.Exists(DatabasePath))
                {
                    System.IO.File.Delete(DatabasePath);
                }
            }
            catch (System.IO.IOException)
            {
            }
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Timed out after {timeout.TotalSeconds}s waiting for: {what}");
    }

    [Fact]
    public async Task MaxConcurrentRunsOne_PinsSerialBehavior_RunsNeverOverlap()
    {
        var options = new ExecutionOptions { MaxConcurrentRuns = 1 };
        await using var harness = await Harness.CreateAsync(runCount: 2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await harness.Worker.StartAsync(cts.Token);

        // The first run must reach the blocking node; the second must NOT while the first holds the
        // only slot — that is the serial guarantee.
        await WaitUntilAsync(() => Task.FromResult(harness.Gate.Arrivals >= 1), TimeSpan.FromSeconds(10), "first run to start");
        await Task.Delay(700); // several poll cycles' worth of opportunity to (incorrectly) start run 2
        Assert.Equal(1, harness.Gate.Arrivals);
        Assert.Equal(1, harness.Gate.MaxObservedConcurrency);

        harness.Gate.Release.TrySetResult();

        await WaitUntilAsync(
            async () => (await harness.GetRunStatusesAsync()).All(s => s == ExecutionStatus.Completed),
            TimeSpan.FromSeconds(10),
            "both runs to complete");

        // Even across the whole test, no two runs ever executed at once.
        Assert.Equal(1, harness.Gate.MaxObservedConcurrency);
        Assert.Equal(2, harness.Gate.Arrivals);
    }

    [Fact]
    public async Task ParallelRuns_OverlapUpToTheConfiguredLimit()
    {
        var options = new ExecutionOptions { MaxConcurrentRuns = 4 };
        // Exercise the batching journal writer on the live path too: three concurrent runs stream
        // their journals through one buffered writer while overlapping.
        await using var harness = await Harness.CreateAsync(runCount: 3, options, useBatchingWriter: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await harness.Worker.StartAsync(cts.Token);

        // All three runs must be inside their blocking node AT THE SAME TIME — impossible under the
        // old serial drain, which awaited each run to completion before dequeuing the next.
        await WaitUntilAsync(() => Task.FromResult(harness.Gate.Arrivals >= 3), TimeSpan.FromSeconds(10), "all three runs to block concurrently");
        Assert.Equal(3, harness.Gate.MaxObservedConcurrency);

        harness.Gate.Release.TrySetResult();

        await WaitUntilAsync(
            async () => (await harness.GetRunStatusesAsync()).All(s => s == ExecutionStatus.Completed),
            TimeSpan.FromSeconds(10),
            "all runs to complete");
    }

    [Fact]
    public async Task RunSlots_BoundConcurrency_ExcessRunStaysPending()
    {
        var options = new ExecutionOptions { MaxConcurrentRuns = 2 };
        await using var harness = await Harness.CreateAsync(runCount: 3, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await harness.Worker.StartAsync(cts.Token);

        await WaitUntilAsync(() => Task.FromResult(harness.Gate.Arrivals >= 2), TimeSpan.FromSeconds(10), "two runs to occupy both slots");
        await Task.Delay(700); // opportunity for a third run to (incorrectly) start
        Assert.Equal(2, harness.Gate.Arrivals);
        Assert.Equal(2, harness.Gate.MaxObservedConcurrency);

        // The third run is still queued — untouched in Pending, not parked half-started.
        var statuses = await harness.GetRunStatusesAsync();
        Assert.Equal(1, statuses.Count(s => s == ExecutionStatus.Pending));

        harness.Gate.Release.TrySetResult();

        await WaitUntilAsync(
            async () => (await harness.GetRunStatusesAsync()).All(s => s == ExecutionStatus.Completed),
            TimeSpan.FromSeconds(10),
            "all three runs to complete");
        Assert.Equal(3, harness.Gate.Arrivals);
        Assert.Equal(2, harness.Gate.MaxObservedConcurrency);
    }
}
