// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Execution;
using Knotarium.Features.Polling;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Knotarium.Tests.Polling;

/// <summary>
/// End-to-end integration tests wiring real components together:
/// HttpPollSource (stub transport) → PollEvaluationService → PollRunEnqueuer → SQLite AppDbContext.
/// </summary>
public class PollingEndToEndTests
{
    // ── Minimal local doubles for HTTP transport ──────────────────────────────

    private sealed class QueuedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        public QueuedHandler(IEnumerable<string> bodies) => _bodies = new Queue<string>(bodies);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = _bodies.Dequeue();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new HttpClient(_handler, disposeHandler: false);
    }

    private sealed class NullSecretResolver : ISecretResolver
    {
        public Task<string?> ResolveAsync(string secretRef, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    // ── Shared seeding helpers ────────────────────────────────────────────────

    private static readonly WorkflowDefinitionId WfId = new("e2e-wf-1");
    private const string TriggerConfig =
        "{\"sourceKind\":\"http\",\"changeDetection\":\"hash\",\"url\":\"https://x.test/a\"}";

    private static async Task SeedWorkflowWithActiveVersionAsync(AppDbContext db, TimeProvider time)
    {
        var nodes = new[]
        {
            new NodeDefinition(NodeId.Create("start"), "pollingTrigger", new Dictionary<string, object>()),
            new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>())
        };
        var edges = new[]
        {
            new EdgeDefinition("e1", NodeId.Create("start"), "result", NodeId.Create("end"), "in")
        };

        var versionId = WorkflowVersionId.New();

        db.WorkflowDefinitions.Add(new WorkflowDefinition(WfId, "E2E Workflow", nodes, edges)
        {
            IsEnabled = true
        });

        db.WorkflowVersions.Add(new WorkflowVersion(versionId, WfId, 1, nodes, edges, time.GetUtcNow()));

        // Insert the active-version mapping directly — mirrors WorkflowEnqueueServiceTests.
        db.ActiveWorkflowVersions.Add(new ActiveWorkflowVersion
        {
            WorkflowDefinitionId = WfId,
            WorkflowVersionId = versionId,
            ActivatedAtUtc = time.GetUtcNow()
        });

        await db.SaveChangesAsync();
    }

    private static async Task SeedTriggerAsync(AppDbContext db, DateTimeOffset nextPoll)
    {
        db.PollingTriggers.Add(new PollingTrigger
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = WfId,
            IntervalSeconds = 60,
            NextPollAtUtc = nextPoll,
            ConfigJson = TriggerConfig,
            Cursor = null,
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    // ── Service factory ───────────────────────────────────────────────────────

    private static (PollEvaluationService service, PollRunEnqueuer enqueuer, WorkflowExecutionQueue queue)
        BuildServices(AppDbContext db, FixedTimeProvider time, HttpMessageHandler handler)
    {
        var queue = new WorkflowExecutionQueue();
        var activeVersionService = new ActiveWorkflowVersionService(db, time);
        var enqueuer = new PollRunEnqueuer(db, queue, activeVersionService, time);
        var pollSource = new HttpPollSource(new StubFactory(handler), new NullSecretResolver());
        var registry = new PollSourceRegistry(new IPollSource[] { pollSource });
        var service = new PollEvaluationService(new DbPollingTriggerStore(db), registry, enqueuer, time, NullLogger<PollEvaluationService>.Instance);
        return (service, enqueuer, queue);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Three polls: body1={"v":1} (new), body2={"v":1} (unchanged), body3={"v":2} (changed).
    /// Expected: exactly 2 ExecutionInstances with TriggerOrigin=="poll", each carrying the payload key.
    /// </summary>
    [Fact]
    public async Task ThreePolls_TwoChanges_EnqueuesTwoRuns()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var t0 = DateTimeOffset.UnixEpoch.AddSeconds(1000);
            var time = new FixedTimeProvider(t0);

            // Seed
            using (var seed = new AppDbContext(options))
            {
                await SeedWorkflowWithActiveVersionAsync(seed, time);
                await SeedTriggerAsync(seed, nextPoll: DateTimeOffset.UnixEpoch); // already due
            }

            var handler = new QueuedHandler(new[] { "{\"v\":1}", "{\"v\":1}", "{\"v\":2}" });

            // Share ONE AppDbContext for enqueuer + evaluation service (mirrors scoped DI).
            using var db = new AppDbContext(options);
            var (service, _, queue) = BuildServices(db, time, handler);

            // ── Poll 1 ─ body {"v":1} is NEW (no prior cursor) ──────────────
            await service.EvaluateDuePollsAsync(CancellationToken.None);

            // Advance time past next scheduled poll (interval=60s) so poll 2 fires.
            time.Advance(TimeSpan.FromSeconds(65));

            // ── Poll 2 ─ same body {"v":1} → UNCHANGED ───────────────────────
            await service.EvaluateDuePollsAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(65));

            // ── Poll 3 ─ body {"v":2} is CHANGED ────────────────────────────
            await service.EvaluateDuePollsAsync(CancellationToken.None);

            // ── Assert: exactly 2 execution instances ────────────────────────
            using var verify = new AppDbContext(options);
            var runs = await verify.ExecutionInstances
                .Where(e => e.TriggerOrigin == "poll")
                .ToListAsync();

            Assert.Equal(2, runs.Count);

            // Each run must carry the payload variable.
            foreach (var run in runs)
            {
                Assert.True(
                    run.GlobalVariables.ContainsKey(PollRunEnqueuer.PayloadVariableKey),
                    $"Run {run.Id} is missing the poll payload variable.");
            }

            // The in-memory queue should also have received 2 execution IDs.
            int queued = 0;
            while (queue.TryDequeue(out _)) queued++;
            Assert.Equal(2, queued);
        }
        finally { connection.Dispose(); }
    }

    /// <summary>
    /// Two polls with identical body → only 1 run (the first poll creates it; the second does not).
    /// </summary>
    [Fact]
    public async Task TwoPolls_SameBody_EnqueuesExactlyOneRun()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var t0 = DateTimeOffset.UnixEpoch.AddSeconds(500);
            var time = new FixedTimeProvider(t0);

            using (var seed = new AppDbContext(options))
            {
                await SeedWorkflowWithActiveVersionAsync(seed, time);
                await SeedTriggerAsync(seed, nextPoll: DateTimeOffset.UnixEpoch);
            }

            var handler = new QueuedHandler(new[] { "{\"stable\":true}", "{\"stable\":true}" });

            using var db = new AppDbContext(options);
            var (service, _, queue) = BuildServices(db, time, handler);

            await service.EvaluateDuePollsAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(65));

            await service.EvaluateDuePollsAsync(CancellationToken.None);

            using var verify = new AppDbContext(options);
            var runs = await verify.ExecutionInstances
                .Where(e => e.TriggerOrigin == "poll")
                .ToListAsync();

            Assert.Single(runs);
            Assert.True(runs[0].GlobalVariables.ContainsKey(PollRunEnqueuer.PayloadVariableKey));

            int queued = 0;
            while (queue.TryDequeue(out _)) queued++;
            Assert.Equal(1, queued);
        }
        finally { connection.Dispose(); }
    }

    /// <summary>
    /// A disabled workflow is never polled — zero runs regardless of body changes.
    /// </summary>
    [Fact]
    public async Task DisabledWorkflow_NeverPolled_ZeroRuns()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var t0 = DateTimeOffset.UnixEpoch.AddSeconds(500);
            var time = new FixedTimeProvider(t0);

            // Seed workflow with IsEnabled = false.
            using (var seed = new AppDbContext(options))
            {
                var nodes = new[]
                {
                    new NodeDefinition(NodeId.Create("start"), "pollingTrigger", new Dictionary<string, object>()),
                    new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>())
                };
                var edges = new[]
                {
                    new EdgeDefinition("e1", NodeId.Create("start"), "result", NodeId.Create("end"), "in")
                };
                var versionId = WorkflowVersionId.New();
                var disabledWfId = new WorkflowDefinitionId("e2e-disabled-wf");

                seed.WorkflowDefinitions.Add(new WorkflowDefinition(disabledWfId, "Disabled Workflow", nodes, edges)
                {
                    IsEnabled = false
                });
                seed.WorkflowVersions.Add(new WorkflowVersion(versionId, disabledWfId, 1, nodes, edges, time.GetUtcNow()));
                seed.ActiveWorkflowVersions.Add(new ActiveWorkflowVersion
                {
                    WorkflowDefinitionId = disabledWfId,
                    WorkflowVersionId = versionId,
                    ActivatedAtUtc = time.GetUtcNow()
                });
                seed.PollingTriggers.Add(new PollingTrigger
                {
                    Id = Guid.NewGuid(),
                    WorkflowDefinitionId = disabledWfId,
                    IntervalSeconds = 60,
                    NextPollAtUtc = DateTimeOffset.UnixEpoch,
                    ConfigJson = TriggerConfig,
                    Cursor = null,
                    IsActive = true
                });
                await seed.SaveChangesAsync();
            }

            var handler = new QueuedHandler(new[] { "{\"v\":1}", "{\"v\":2}" });

            using var db = new AppDbContext(options);
            var (service, _, queue) = BuildServices(db, time, handler);

            await service.EvaluateDuePollsAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(65));
            await service.EvaluateDuePollsAsync(CancellationToken.None);

            using var verify = new AppDbContext(options);
            var runs = await verify.ExecutionInstances
                .Where(e => e.TriggerOrigin == "poll")
                .ToListAsync();

            Assert.Empty(runs);

            int queued = 0;
            while (queue.TryDequeue(out _)) queued++;
            Assert.Equal(0, queued);
        }
        finally { connection.Dispose(); }
    }
}
