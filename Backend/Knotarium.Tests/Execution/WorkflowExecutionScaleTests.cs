using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Knotarium.Features.Execution;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Security;
using Xunit;
using Xunit.Abstractions;

namespace Knotarium.Tests.Execution;

/// <summary>
/// A configurable throughput/scaling harness that drives the REAL execution engine — real node tasks
/// (start / forLoop / log / delay / end), a real on-disk SQLite database, the real journal-batching
/// writer, and the real <see cref="WorkflowExecutionWorker"/> dispatch loop. It runs many workflow runs
/// at once and reports wall-clock, throughput, peak observed concurrency, and journal-write volume.
///
/// It is OFF by default (it would write millions of rows and take minutes) — set the env var
/// <c>KNOT_SCALE=1</c> to run it. Everything is env-configurable:
/// <list type="bullet">
///   <item><c>KNOT_SCALE_RUNS</c> — number of concurrent workflow runs (default 100).</item>
///   <item><c>KNOT_SCALE_MODE</c> — <c>write</c> (a forLoop that logs N times per run — the write/batching
///     stress) or <c>io</c> (a delay per run — the parallelism/overlap proof). Default <c>write</c>.</item>
///   <item><c>KNOT_SCALE_ITERATIONS</c> — loop count per run in <c>write</c> mode (default 500).</item>
///   <item><c>KNOT_SCALE_DELAYMS</c> — per-run delay in <c>io</c> mode (default 200).</item>
///   <item><c>KNOT_SCALE_MAXCONCURRENT</c> — the run-slot count under test (default 4; set 1 for the
///     serial baseline).</item>
///   <item><c>KNOT_SCALE_BATCHING</c> — <c>on</c>/<c>off</c> to compare journal batching (default on).</item>
///   <item><c>KNOT_SCALE_TIMEOUT_SECONDS</c> — overall wait budget (default 600).</item>
/// </list>
///
/// <para><b>Which mode measures what.</b> A workflow that only loops and logs is <i>write-bound</i>: on
/// SQLite there is exactly one global writer, so 100 such runs mostly serialize on the write lock and
/// raising <c>MaxConcurrentRuns</c> will NOT cut wall-clock much — what it stresses is journal-write
/// throughput and the batching layer (compare <c>KNOT_SCALE_BATCHING=on</c> vs <c>off</c>). To actually
/// see run-level parallelism, use <c>io</c> mode: with a per-run delay, wall-clock drops from
/// ~<c>runs × delay</c> at <c>MAXCONCURRENT=1</c> toward ~<c>ceil(runs / K) × delay</c> at <c>K</c>.</para>
/// </summary>
[Collection(WorkflowExecutionIsolationCollection.Name)]
[Trait("Category", "Scale")]
public class WorkflowExecutionScaleTests
{
    private readonly ITestOutputHelper _output;

    public WorkflowExecutionScaleTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string? Env(string key) => Environment.GetEnvironmentVariable(key);

    private static int EnvInt(string key, int fallback) =>
        int.TryParse(Env(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Counts what the batching layer actually flushes, so the report can show batch efficiency.</summary>
    private sealed class CountingJournalWriter : IExecutionJournalWriter
    {
        private readonly IExecutionJournalWriter _inner;
        private long _singleWrites;
        private long _batchFlushes;
        private long _batchedRows;

        public CountingJournalWriter(IExecutionJournalWriter inner) => _inner = inner;

        public long SingleWrites => Interlocked.Read(ref _singleWrites);
        public long BatchFlushes => Interlocked.Read(ref _batchFlushes);
        public long BatchedRows => Interlocked.Read(ref _batchedRows);
        public long TotalRows => SingleWrites + BatchedRows;

        public Task WriteAsync(ExecutionJournal entry)
        {
            Interlocked.Increment(ref _singleWrites);
            return _inner.WriteAsync(entry);
        }

        public Task WriteBatchAsync(IReadOnlyList<ExecutionJournal> entries)
        {
            Interlocked.Increment(ref _batchFlushes);
            Interlocked.Add(ref _batchedRows, entries.Count);
            return _inner.WriteBatchAsync(entries);
        }
    }

    private sealed class FakeEventPublisher : IExecutionEventPublisher
    {
        public Task PublishAsync(ExecutionInstanceId executionId, ExecutionJournal entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task RunScale()
    {
        // Opt-in only: it writes millions of rows and takes minutes. Without the flag it is an instant no-op
        // so it never disrupts the normal suite. Run with: KNOT_SCALE=1 dotnet test --filter Category=Scale
        if (Env("KNOT_SCALE") != "1")
        {
            _output.WriteLine("Scale harness skipped: set KNOT_SCALE=1 to run it.");
            return;
        }

        var runs = EnvInt("KNOT_SCALE_RUNS", 100);
        var mode = (Env("KNOT_SCALE_MODE") ?? "write").Trim().ToLowerInvariant();
        var iterations = EnvInt("KNOT_SCALE_ITERATIONS", 500);
        var delayMs = EnvInt("KNOT_SCALE_DELAYMS", 200);
        var maxConcurrent = EnvInt("KNOT_SCALE_MAXCONCURRENT", 4);
        var batching = (Env("KNOT_SCALE_BATCHING") ?? "on").Trim().ToLowerInvariant() != "off";
        var timeoutSeconds = EnvInt("KNOT_SCALE_TIMEOUT_SECONDS", 600);

        var options = new ExecutionOptions
        {
            MaxConcurrentRuns = maxConcurrent,
            MaxQueueDepth = Math.Max(runs * 2, 1000),
            JournalBatchingEnabled = batching,
        };

        var databasePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"knotarium-scale-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";

        try
        {
            await SeedWorkflowsAsync(connectionString, runs, mode, iterations, delayMs);

            var manifestProvider = new InMemoryNodePackageManifestProvider();
            var counting = new CountingJournalWriter(new SqliteExecutionJournalWriter(connectionString));
            IExecutionJournalWriter journalWriter = counting;
            BatchingExecutionJournalWriter? batchingWriter = null;
            if (batching)
            {
                batchingWriter = new BatchingExecutionJournalWriter(counting, options);
                journalWriter = batchingWriter;
            }

            var monitor = new ExecutionRuntimeMonitor();

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning)); // silence per-iteration Log node output
            services.AddSingleton(manifestProvider);
            services.AddSingleton<INodePackageManifestProvider>(manifestProvider);
            services.AddSingleton<IExecutionEventPublisher, FakeEventPublisher>();
            services.AddSingleton<ExecutionTelemetry>();
            services.AddSingleton<ICorrelationTokenCrypto, CorrelationTokenCrypto>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(options);
            services.AddSingleton(monitor);
            services.AddSingleton(new WorkflowExecutionQueue(options));
            services.AddSingleton(journalWriter);
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));
            services.AddScoped<DatabaseWorkflowStore>();
            services.AddScoped<IWorkflowStore>(sp => sp.GetRequiredService<DatabaseWorkflowStore>());
            services.AddScoped<IWorkflowDefinitionProvider>(sp => sp.GetRequiredService<DatabaseWorkflowStore>());
            services.AddScoped<WorkflowCompiler>();
            services.AddScoped<WorkflowExecutor>();
            services.AddScoped<RecoveryService>();
            services.AddBuiltInNodes(); // the REAL node-task registry + real start/forLoop/log/delay/end tasks

            using var serviceProvider = services.BuildServiceProvider();
            var worker = new WorkflowExecutionWorker(
                serviceProvider.GetRequiredService<WorkflowExecutionQueue>(),
                serviceProvider,
                serviceProvider.GetRequiredService<ILogger<WorkflowExecutionWorker>>(),
                options,
                monitor);

            // Sample peak observed concurrency while the batch runs.
            long peakConcurrency = 0;
            using var sampler = new CancellationTokenSource();
            var samplerTask = Task.Run(async () =>
            {
                while (!sampler.IsCancellationRequested)
                {
                    peakConcurrency = Math.Max(peakConcurrency, monitor.InFlightRuns);
                    try { await Task.Delay(10, sampler.Token); } catch (OperationCanceledException) { break; }
                }
            });

            _output.WriteLine($"=== Scale run: {runs} workflows, mode={mode}, " +
                              (mode == "io" ? $"delay={delayMs}ms, " : $"iterations={iterations}, ") +
                              $"MaxConcurrentRuns={maxConcurrent}, batching={(batching ? "on" : "off")} ===");

            var stopwatch = Stopwatch.StartNew();
            using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            await worker.StartAsync(runCts.Token); // startup recovery re-queues the seeded Pending runs

            var completed = await WaitForTerminalAsync(connectionString, runs, TimeSpan.FromSeconds(timeoutSeconds));
            stopwatch.Stop();

            await worker.StopAsync(CancellationToken.None);
            if (batchingWriter != null)
            {
                await batchingWriter.DisposeAsync(); // drain any buffered trace entries before counting rows
            }
            sampler.Cancel();
            await samplerTask;

            var totalJournalRows = await CountJournalRowsAsync(connectionString);
            var elapsed = stopwatch.Elapsed;

            _output.WriteLine($"Completed runs        : {completed}/{runs}" + (completed < runs ? "  (TIMED OUT)" : ""));
            _output.WriteLine($"Wall-clock            : {elapsed.TotalSeconds:F2}s");
            _output.WriteLine($"Throughput            : {completed / Math.Max(elapsed.TotalSeconds, 0.001):F1} runs/s");
            _output.WriteLine($"Peak concurrent runs  : {peakConcurrency}  (limit {maxConcurrent})");
            _output.WriteLine($"Journal rows (DB)     : {totalJournalRows:N0}");
            _output.WriteLine($"Journal write rate    : {totalJournalRows / Math.Max(elapsed.TotalSeconds, 0.001):N0} rows/s");
            if (batching)
            {
                var avgBatch = counting.BatchFlushes > 0 ? (double)counting.BatchedRows / counting.BatchFlushes : 0;
                _output.WriteLine($"Batched rows          : {counting.BatchedRows:N0} in {counting.BatchFlushes:N0} flushes (avg {avgBatch:F1} rows/flush)");
                _output.WriteLine($"Critical single writes: {counting.SingleWrites:N0} (awaited-to-disk durability points)");
            }
            else
            {
                _output.WriteLine($"Row-by-row writes     : {counting.SingleWrites:N0} (batching OFF)");
            }

            if (mode == "io")
            {
                var idealSerial = runs * (delayMs / 1000.0);
                var idealParallel = Math.Ceiling((double)runs / maxConcurrent) * (delayMs / 1000.0);
                _output.WriteLine($"Overlap check (io)    : serial≈{idealSerial:F1}s, ideal@{maxConcurrent}≈{idealParallel:F1}s, actual={elapsed.TotalSeconds:F2}s");
            }

            Assert.Equal(runs, completed);
            if (maxConcurrent > 1)
            {
                Assert.True(peakConcurrency > 1,
                    $"Expected concurrent overlap with MaxConcurrentRuns={maxConcurrent}, but peak observed concurrency was {peakConcurrency}.");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { System.IO.File.Delete(databasePath + suffix); } catch (System.IO.IOException) { }
            }
        }
    }

    private static async Task SeedWorkflowsAsync(string connectionString, int runs, string mode, int iterations, int delayMs)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        await using var context = new AppDbContext(dbOptions);
        await context.Database.EnsureCreatedAsync();

        for (var i = 0; i < runs; i++)
        {
            var workflowId = WorkflowDefinitionId.New();
            WorkflowDefinition workflow = mode == "io"
                ? BuildDelayWorkflow(workflowId, i, delayMs)
                : BuildLoopWorkflow(workflowId, i, iterations);

            var version = new WorkflowVersion(WorkflowVersionId.New(), workflowId, 1, workflow.Nodes, workflow.Edges, DateTimeOffset.UtcNow);
            context.WorkflowDefinitions.Add(workflow);
            context.WorkflowVersions.Add(version);
            context.ExecutionInstances.Add(new ExecutionInstance
            {
                Id = ExecutionInstanceId.New(),
                WorkflowDefinitionId = workflowId,
                WorkflowVersionId = version.Id,
                Status = ExecutionStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow.AddMilliseconds(i),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await context.SaveChangesAsync();
    }

    // start -> forLoop(count=iterations) --start--> log --result(loop-back)--> forLoop.end
    //                                     --success--> end
    private static WorkflowDefinition BuildLoopWorkflow(WorkflowDefinitionId id, int index, int iterations)
    {
        var start = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var loop = new NodeDefinition(NodeId.Create("loop"), "forLoop", new Dictionary<string, object>
        {
            ["mode"] = "count",
            ["count"] = iterations,
        });
        var body = new NodeDefinition(NodeId.Create("body"), "log", new Dictionary<string, object>
        {
            ["message"] = $"run {index} iteration",
        });
        var end = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());

        return new WorkflowDefinition(
            id,
            $"Scale Loop {index}",
            new[] { start, loop, body, end },
            new[]
            {
                new EdgeDefinition("e-start", start.Id, "result", loop.Id, "in"),
                new EdgeDefinition("e-iter", loop.Id, "start", body.Id, "in"),
                new EdgeDefinition("e-loopback", body.Id, "result", loop.Id, "end"),
                new EdgeDefinition("e-exit", loop.Id, "success", end.Id, "in"),
            });
    }

    // start -> delay(delayMs) -> end
    private static WorkflowDefinition BuildDelayWorkflow(WorkflowDefinitionId id, int index, int delayMs)
    {
        var start = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var delay = new NodeDefinition(NodeId.Create("delay"), "delay", new Dictionary<string, object>
        {
            ["delayMs"] = delayMs,
        });
        var end = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());

        return new WorkflowDefinition(
            id,
            $"Scale Delay {index}",
            new[] { start, delay, end },
            new[]
            {
                new EdgeDefinition("e-start", start.Id, "result", delay.Id, "in"),
                new EdgeDefinition("e-end", delay.Id, "result", end.Id, "in"),
            });
    }

    private static async Task<int> WaitForTerminalAsync(string connectionString, int runs, TimeSpan timeout)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using var context = new AppDbContext(dbOptions);
            var terminal = await context.ExecutionInstances.CountAsync(e =>
                e.Status == ExecutionStatus.Completed ||
                e.Status == ExecutionStatus.Failed ||
                e.Status == ExecutionStatus.Cancelled ||
                e.Status == ExecutionStatus.Discarded);

            if (terminal >= runs)
            {
                return terminal;
            }

            await Task.Delay(100);
        }

        await using var finalContext = new AppDbContext(dbOptions);
        return await finalContext.ExecutionInstances.CountAsync(e =>
            e.Status == ExecutionStatus.Completed ||
            e.Status == ExecutionStatus.Failed ||
            e.Status == ExecutionStatus.Cancelled ||
            e.Status == ExecutionStatus.Discarded);
    }

    private static async Task<long> CountJournalRowsAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM JournalEntries;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
