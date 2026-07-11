using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Polling;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Knotarium.Tests.Polling;

public class PollEvaluationServiceTests
{
    private sealed class ScriptedSource : IPollSource
    {
        private readonly PollResult _result;
        public ScriptedSource(PollResult result) => _result = result;
        public string Kind => "http";
        public int Calls { get; private set; }
        public Task<PollResult> PollAsync(PollContext c, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingEnqueueService : IPollRunEnqueuer
    {
        public int EnqueueCount { get; private set; }
        public Task<bool> EnqueueAsync(WorkflowDefinitionId workflowId, object? payload, CancellationToken ct)
        {
            EnqueueCount++;
            return Task.FromResult(true);
        }
    }

    private sealed class ThrowingSource : IPollSource
    {
        public string Kind => "http";
        public Task<PollResult> PollAsync(PollContext c, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class NoActiveVersionEnqueueService : IPollRunEnqueuer
    {
        public int Calls { get; private set; }
        public Task<bool> EnqueueAsync(WorkflowDefinitionId workflowId, object? payload, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(false); // simulates a workflow with no active version
        }
    }

    private static async Task SeedWorkflowAsync(AppDbContext db, bool enabled)
    {
        await db.WorkflowDefinitions.AddAsync(new WorkflowDefinition(
            new WorkflowDefinitionId("wf-1"),
            "wf",
            new List<NodeDefinition>(),
            new List<EdgeDefinition>())
        { IsEnabled = enabled });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedTriggerAsync(AppDbContext db, DateTimeOffset nextPoll)
    {
        var id = Guid.NewGuid();
        await db.PollingTriggers.AddAsync(new PollingTrigger
        {
            Id = id,
            WorkflowDefinitionId = new WorkflowDefinitionId("wf-1"),
            IntervalSeconds = 60,
            NextPollAtUtc = nextPoll,
            ConfigJson = "{\"sourceKind\":\"http\",\"changeDetection\":\"hash\",\"url\":\"https://x.test/a\"}",
            Cursor = null,
            IsActive = true
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static PollEvaluationService CreateService(
        DbContextOptions<AppDbContext> options, FixedTimeProvider time, IPollSource source, IPollRunEnqueuer enqueue)
    {
        var db = new AppDbContext(options);
        var registry = new PollSourceRegistry(new[] { source });
        return new PollEvaluationService(new DbPollingTriggerStore(db), registry, enqueue, time, NullLogger<PollEvaluationService>.Instance);
    }

    [Fact]
    public async Task HasNew_True_EnqueuesAndAdvances_AndStoresCursor()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1000));
            using (var seed = new AppDbContext(options))
            {
                await SeedWorkflowAsync(seed, enabled: true);
                await SeedTriggerAsync(seed, nextPoll: DateTimeOffset.UnixEpoch);
            }

            var source = new ScriptedSource(new PollResult(HasNew: true, Payload: "{\"v\":1}", NewCursor: "cur-1"));
            var enqueue = new RecordingEnqueueService();
            var service = CreateService(options, time, source, enqueue);

            await service.EvaluateDuePollsAsync(CancellationToken.None);

            using var verify = new AppDbContext(options);
            var row = await verify.PollingTriggers.SingleAsync();
            Assert.Equal("cur-1", row.Cursor);
            Assert.Equal(time.GetUtcNow().AddSeconds(60), row.NextPollAtUtc);
            Assert.Equal(1, enqueue.EnqueueCount);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task HasNew_False_NoEnqueue_StillAdvances()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1000));
            using (var seed = new AppDbContext(options))
            {
                await SeedWorkflowAsync(seed, enabled: true);
                await SeedTriggerAsync(seed, nextPoll: DateTimeOffset.UnixEpoch);
            }

            var source = new ScriptedSource(new PollResult(HasNew: false, Payload: null, NewCursor: null));
            var enqueue = new RecordingEnqueueService();
            var service = CreateService(options, time, source, enqueue);

            await service.EvaluateDuePollsAsync(CancellationToken.None);

            using var verify = new AppDbContext(options);
            var row = await verify.PollingTriggers.SingleAsync();
            Assert.Equal(time.GetUtcNow().AddSeconds(60), row.NextPollAtUtc);
            Assert.Equal(0, enqueue.EnqueueCount);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task DisabledWorkflow_NotPolled()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1000));
            using (var seed = new AppDbContext(options))
            {
                await SeedWorkflowAsync(seed, enabled: false);
                await SeedTriggerAsync(seed, nextPoll: DateTimeOffset.UnixEpoch);
            }

            var source = new ScriptedSource(new PollResult(true, "x", "cur"));
            var enqueue = new RecordingEnqueueService();
            var service = CreateService(options, time, source, enqueue);

            await service.EvaluateDuePollsAsync(CancellationToken.None);

            Assert.Equal(0, source.Calls);
            Assert.Equal(0, enqueue.EnqueueCount);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task HasNew_ButNoActiveVersion_DoesNotAdvanceCursor_StillAdvancesTimer()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1000));
            using (var seed = new AppDbContext(options))
            {
                await SeedWorkflowAsync(seed, enabled: true);
                await SeedTriggerAsync(seed, nextPoll: DateTimeOffset.UnixEpoch);
            }

            var source = new ScriptedSource(new PollResult(HasNew: true, Payload: "{\"v\":1}", NewCursor: "cur-1"));
            var enqueue = new NoActiveVersionEnqueueService();
            var service = CreateService(options, time, source, enqueue);

            await service.EvaluateDuePollsAsync(CancellationToken.None);

            using var verify = new AppDbContext(options);
            var row = await verify.PollingTriggers.SingleAsync();
            Assert.Equal(1, enqueue.Calls);
            Assert.Null(row.Cursor); // cursor must NOT advance when no run was created
            Assert.Equal(time.GetUtcNow().AddSeconds(60), row.NextPollAtUtc);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task SourceThrows_RecordsLastError_AndAdvances_WithoutAborting()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1000));
            using (var seed = new AppDbContext(options))
            {
                await SeedWorkflowAsync(seed, enabled: true);
                await SeedTriggerAsync(seed, nextPoll: DateTimeOffset.UnixEpoch);
            }

            var enqueue = new RecordingEnqueueService();
            var service = CreateService(options, time, new ThrowingSource(), enqueue);

            // Must not throw out of the evaluation loop.
            await service.EvaluateDuePollsAsync(CancellationToken.None);

            using var verify = new AppDbContext(options);
            var row = await verify.PollingTriggers.SingleAsync();
            Assert.Contains("boom", row.LastError);
            Assert.Equal(time.GetUtcNow().AddSeconds(60), row.NextPollAtUtc); // advanced: no hammering
            Assert.Equal(0, enqueue.EnqueueCount);
        }
        finally { connection.Dispose(); }
    }
}
