// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;
using Knotarium.Features.Execution;
using Knotarium.Features.Notifications;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Tests.Polling;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Knotarium.Tests.ErrorWorkflow;

public class ErrorWorkflowDispatchTests
{
    // ── Loop-prevention invariant (A0) ───────────────────────────────────────

    [Fact]
    public void ShouldStart_True_ForNormalFailedRun()
        => Assert.True(ErrorWorkflowWorker.ShouldStartErrorWorkflow("wf-business", "manual", "wf-handler"));

    [Fact]
    public void ShouldStart_False_WhenFailedRunIsTheErrorWorkflow()
        => Assert.False(ErrorWorkflowWorker.ShouldStartErrorWorkflow("wf-handler", "manual", "wf-handler"));

    [Fact]
    public void ShouldStart_False_WhenFailedRunWasItselfAnErrorHandler()
        => Assert.False(ErrorWorkflowWorker.ShouldStartErrorWorkflow("wf-other-handler", "error", "wf-handler"));

    // ── Failure-context payload shape (consumed on errorTrigger.result) ──────

    [Fact]
    public void Payload_CarriesFullFailureContext_IncludingNodeType()
    {
        var context = new FailureAlertMessage(
            WorkflowName: "Orders",
            WorkflowId: "wf-business",
            ExecutionId: "exec-1",
            FailedNodeId: "node-7",
            ErrorMessage: "simulated failure",
            TriggerOrigin: "manual",
            TimestampUtc: DateTimeOffset.UnixEpoch);

        var payload = ErrorWorkflowWorker.BuildPayload(context, failedNodeType: "inlineCode");

        Assert.Equal("wf-business", payload["workflowId"]);
        Assert.Equal("node-7", payload["failedNodeId"]);
        Assert.Equal("inlineCode", payload["failedNodeType"]);
        Assert.Equal("simulated failure", payload["errorMessage"]);
        Assert.Equal("manual", payload["triggerOrigin"]);
    }

    [Fact]
    public void Globals_ExposeEachFailureField_ForDirectUse()
    {
        var context = new FailureAlertMessage(
            WorkflowName: "Orders",
            WorkflowId: "wf-business",
            ExecutionId: "exec-1",
            FailedNodeId: "node-7",
            ErrorMessage: "simulated failure",
            TriggerOrigin: "manual",
            TimestampUtc: DateTimeOffset.UnixEpoch);

        var globals = ErrorWorkflowWorker.BuildGlobals(context, failedNodeType: "inlineCode");

        Assert.Equal("wf-business", globals["errorWorkflowId"]);
        Assert.Equal("Orders", globals["errorWorkflowName"]);
        Assert.Equal("node-7", globals["errorFailedNodeId"]);
        Assert.Equal("inlineCode", globals["errorFailedNodeType"]);
        Assert.Equal("simulated failure", globals["errorMessage"]);
    }

    [Fact]
    public async Task Build_RecoversFailedNode_FromJournal_WhenNodeStateNotYetCommitted()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var execId = ExecutionInstanceId.New();
            var wfId = new WorkflowDefinitionId("wf-business");

            using (var seed = new AppDbContext(options))
            {
                // Failed run with NO failed NodeState (simulates the enqueue-before-commit race) …
                seed.ExecutionInstances.Add(new ExecutionInstance
                {
                    Id = execId,
                    WorkflowDefinitionId = wfId,
                    Status = ExecutionStatus.Failed,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    UpdatedAt = DateTimeOffset.UnixEpoch,
                    TriggerOrigin = "manual",
                    GlobalVariables = new Dictionary<string, object>()
                });
                // … but the NodeExecutionFailed journal entry IS committed.
                seed.JournalEntries.Add(new ExecutionJournal
                {
                    Id = Guid.NewGuid(),
                    ExecutionInstanceId = execId,
                    NodeId = NodeId.Create("inlineCode-1"),
                    Timestamp = DateTimeOffset.UnixEpoch,
                    EventType = JournalEventTypes.NodeExecutionFailed,
                    Message = "Node 'inlineCode-1' failed: simulated failure.",
                    Data = new Dictionary<string, object> { ["error"] = "simulated failure" }
                });
                await seed.SaveChangesAsync();
            }

            using var db = new AppDbContext(options);
            var instance = await db.ExecutionInstances.Include(e => e.NodeStates).SingleAsync(e => e.Id == execId);
            var workflow = new WorkflowDefinition(wfId, "Simulate Error",
                new[] { new NodeDefinition(NodeId.Create("inlineCode-1"), "inlineCode", new Dictionary<string, object>()) },
                new List<EdgeDefinition>());

            var ctx = await FailureContextBuilder.BuildAsync(new DbExecutionReadStore(db), instance, workflow, CancellationToken.None);

            Assert.Equal("inlineCode-1", ctx.FailedNodeId);
            Assert.Equal("simulated failure", ctx.ErrorMessage);
            Assert.Equal("inlineCode", ErrorWorkflowWorker.ResolveFailedNodeType(workflow, ctx.FailedNodeId));
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public void ResolveFailedNodeType_FindsTypeById()
    {
        var workflow = new WorkflowDefinition(
            new WorkflowDefinitionId("wf-business"),
            "Orders",
            new[]
            {
                new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>()),
                new NodeDefinition(NodeId.Create("node-7"), "inlineCode", new Dictionary<string, object>()),
            },
            new List<EdgeDefinition>());

        Assert.Equal("inlineCode", ErrorWorkflowWorker.ResolveFailedNodeType(workflow, "node-7"));
        Assert.Null(ErrorWorkflowWorker.ResolveFailedNodeType(workflow, "missing"));
        Assert.Null(ErrorWorkflowWorker.ResolveFailedNodeType(null, "node-7"));
    }

    // ── Enqueuer end-to-end ──────────────────────────────────────────────────

    private static readonly WorkflowDefinitionId HandlerId = new("wf-handler");

    private static async Task SeedHandlerWithActiveVersionAsync(AppDbContext db, TimeProvider time)
    {
        var nodes = new[]
        {
            new NodeDefinition(NodeId.Create("trigger"), "errorTrigger", new Dictionary<string, object>()),
            new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>())
        };
        var edges = new[]
        {
            new EdgeDefinition("e1", NodeId.Create("trigger"), "result", NodeId.Create("end"), "in")
        };
        var versionId = WorkflowVersionId.New();

        db.WorkflowDefinitions.Add(new WorkflowDefinition(HandlerId, "Error Handler", nodes, edges) { IsEnabled = true });
        db.WorkflowVersions.Add(new WorkflowVersion(versionId, HandlerId, 1, nodes, edges, time.GetUtcNow()));
        db.ActiveWorkflowVersions.Add(new ActiveWorkflowVersion
        {
            WorkflowDefinitionId = HandlerId,
            WorkflowVersionId = versionId,
            ActivatedAtUtc = time.GetUtcNow()
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Enqueue_CreatesErrorRun_WithOriginAndPayload()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1000));
            using (var seed = new AppDbContext(options))
            {
                await SeedHandlerWithActiveVersionAsync(seed, time);
            }

            using var db = new AppDbContext(options);
            var queue = new WorkflowExecutionQueue();
            var enqueuer = new ErrorWorkflowRunEnqueuer(db, queue, new ActiveWorkflowVersionService(db, time), time);

            var sourceId = ExecutionInstanceId.New();
            var payload = new Dictionary<string, object?> { ["workflowId"] = "wf-business", ["errorMessage"] = "boom" };
            var globals = new Dictionary<string, object?> { ["errorMessage"] = "boom", ["errorFailedNodeType"] = "inlineCode" };
            var errorRunId = await enqueuer.EnqueueAsync(HandlerId, sourceId, payload, globals, CancellationToken.None);

            Assert.NotNull(errorRunId);

            using var verify = new AppDbContext(options);
            var run = await verify.ExecutionInstances.SingleAsync(e => e.TriggerOrigin == "error");
            Assert.Equal(HandlerId.Value, run.WorkflowDefinitionId.Value);
            Assert.Equal(sourceId, run.ErrorOfExecutionId); // linked back to the failed run
            Assert.True(run.GlobalVariables.ContainsKey(ErrorWorkflowRunEnqueuer.PayloadVariableKey));
            // Flattened fields are first-class globals (usable as {errorMessage} in a Log node).
            // Values round-trip through SQLite as JsonElement, so compare the stringified form
            // (exactly what LogNodeTask's {key} substitution and GetVariable do at runtime).
            Assert.Equal("boom", run.GlobalVariables["errorMessage"]?.ToString());
            Assert.Equal("inlineCode", run.GlobalVariables["errorFailedNodeType"]?.ToString());

            Assert.True(queue.TryDequeue(out var queuedId));
            Assert.Equal(run.Id, queuedId);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task Enqueue_NoOp_WhenErrorWorkflowHasNoActiveVersion()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddSeconds(1000));
            using var db = new AppDbContext(options);
            var queue = new WorkflowExecutionQueue();
            var enqueuer = new ErrorWorkflowRunEnqueuer(db, queue, new ActiveWorkflowVersionService(db, time), time);

            var errorRunId = await enqueuer.EnqueueAsync(new WorkflowDefinitionId("wf-unpublished"), ExecutionInstanceId.New(), payload: null, extraGlobals: null, CancellationToken.None);

            Assert.Null(errorRunId);
            using var verify = new AppDbContext(options);
            Assert.Empty(await verify.ExecutionInstances.Where(e => e.TriggerOrigin == "error").ToListAsync());
            Assert.False(queue.TryDequeue(out _));
        }
        finally { connection.Dispose(); }
    }
}
