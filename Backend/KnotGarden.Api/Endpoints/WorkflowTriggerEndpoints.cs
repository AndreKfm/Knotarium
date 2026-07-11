using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using KnotGarden.Api.Services;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Execution;
using KnotGarden.Infrastructure.Persistence;

namespace KnotGarden.Api;

/// <summary>
/// Workflow enablement + manual triggering: toggle IsEnabled (which cancels in-flight runs and
/// syncs inbound signal subscriptions), start a run from the active version, simulate an inbound
/// device signal seeded at a wired pin's downstream nodes, and manually fire a scheduler node.
/// </summary>
public static class WorkflowTriggerEndpoints
{
    public static void MapWorkflowTriggerEndpoints(this WebApplication app)
    {
        app.MapPost("/api/workflows/{id}/enabled", async (string id, SetEnabledRequest request, IWorkflowStore workflowStore, AppDbContext db, KnotGarden.Api.Services.ExternalSignalTriggerRegistry externalSignalRegistry) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            var workflow = await workflowStore.GetAsync(workflowId);
            if (workflow == null)
            {
                return Results.NotFound(new { message = "Workflow definition not found" });
            }

            // Persist the flag to the draft store (read by trigger endpoints) and the database
            // header (joined by the schedule evaluator). The header only exists once published.
            var updated = workflow with { IsEnabled = request.Enabled };
            await workflowStore.UpdateAsync(updated);

            // Register/unregister inbound external-signal (Event/Action Trigger) subscriptions to match
            // the new enabled state — refcounts on the provider connection are held by these subscriptions.
            await externalSignalRegistry.SyncAsync(updated);

            await db.WorkflowDefinitions
                .Where(definition => definition.Id == workflowId)
                .ExecuteUpdateAsync(updates => updates.SetProperty(definition => definition.IsEnabled, request.Enabled));

            var cancelledCount = 0;
            if (!request.Enabled)
            {
                var now = DateTimeOffset.UtcNow;

                // Cancel in-flight executions. The execution worker observes this status between
                // nodes and stops scheduling further work (the current node, if any, finishes).
                cancelledCount = await db.ExecutionInstances
                    .Where(execution => execution.WorkflowDefinitionId == workflowId &&
                        (execution.Status == ExecutionStatus.Pending ||
                         execution.Status == ExecutionStatus.Running ||
                         execution.Status == ExecutionStatus.Suspended ||
                         execution.Status == ExecutionStatus.WaitingForRetry))
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(execution => execution.Status, ExecutionStatus.Cancelled)
                        .SetProperty(execution => execution.UpdatedAt, now));

                // Drop queued continuation work items for the now-cancelled executions so the worker
                // does not resume them. A subquery keeps the comparison in SQL.
                var cancelledExecutionIds = db.ExecutionInstances
                    .Where(execution => execution.WorkflowDefinitionId == workflowId &&
                        execution.Status == ExecutionStatus.Cancelled)
                    .Select(execution => execution.Id);

                await db.ExecutionWorkItems
                    .Where(workItem => workItem.Status == WorkItemStatus.Pending &&
                        cancelledExecutionIds.Contains(workItem.ExecutionInstanceId))
                    .ExecuteDeleteAsync();
            }

            return Results.Ok(new { id = workflowId.Value, enabled = request.Enabled, cancelledExecutions = cancelledCount });
        });

        app.MapPost("/api/workflows/{id}/trigger", async (string id, IWorkflowStore workflowStore, ExecutionStarter executionStarter, ActiveWorkflowVersionService activeWorkflowVersionService) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            var workflow = await workflowStore.GetAsync(workflowId);
            if (workflow == null)
            {
                return Results.NotFound(new { message = "Workflow definition not found" });
            }

            var activeVersion = await activeWorkflowVersionService.GetActiveVersionAsync(workflowId);
            if (activeVersion is null)
            {
                return Results.Conflict(new { message = "Workflow has no active version. Publish and activate a version before triggering execution." });
            }

            var runtimeWorkflow = new WorkflowDefinition(workflow.Id, workflow.Name, activeVersion.Nodes, activeVersion.Edges);

            var outcome = await executionStarter.StartAsync(runtimeWorkflow, activeVersion.Id, "manual");
            if (!outcome.IsStarted)
            {
                return Results.BadRequest(new
                {
                    message = "Workflow failed compilation and cannot be triggered",
                    diagnostics = outcome.Diagnostics
                });
            }

            return Results.Accepted($"/api/executions/{outcome.Instance!.Id.Value}", outcome.Instance);
        });

        // Simulate an inbound device signal from the editor: start a run seeded at the chosen pin's downstream
        // node(s) with a synthetic envelope — exactly like a live device event — instead of a generic manual run
        // (which would execute the inert device block as a disconnected no-op and never flow from the pin). The
        // caller picks a wired action/event pin and optional sample field values; the run reads `signal.params.*`.
        app.MapPost("/api/workflows/{id}/simulate-signal", async (
            string id,
            SimulateSignalRequest request,
            IWorkflowStore workflowStore,
            ActiveWorkflowVersionService activeWorkflowVersionService,
            KnotGarden.Core.Contracts.IExternalSignalRunEnqueuer enqueuer,
            CancellationToken cancellationToken) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            var workflow = await workflowStore.GetAsync(workflowId);
            if (workflow == null)
            {
                return Results.NotFound(new { message = "Workflow definition not found" });
            }
            if (string.IsNullOrWhiteSpace(request.Type))
            {
                return Results.BadRequest(new { message = "A signal type (action/event id) is required." });
            }

            var activeVersion = await activeWorkflowVersionService.GetActiveVersionAsync(workflowId);
            if (activeVersion is null)
            {
                return Results.Conflict(new { message = "Workflow has no active version. Publish and activate a version before simulating a signal." });
            }

            var runtimeWorkflow = new WorkflowDefinition(workflow.Id, workflow.Name, activeVersion.Nodes, activeVersion.Edges);

            var kind = string.Equals(request.Kind, "event", StringComparison.OrdinalIgnoreCase)
                ? KnotGarden.Core.Contracts.ExternalSignalKind.Event
                : KnotGarden.Core.Contracts.ExternalSignalKind.Action;

            // Resolve the pin's downstream entry node(s) from the compiled signal triggers (the imperative bridge).
            var triggers = KnotGarden.Core.Reactive.ReactiveRuleCompiler.CompileSignalTriggers(runtimeWorkflow);
            var matched = triggers.Where(trigger =>
            {
                if (trigger.Kind != kind) return false;
                var (baseType, _) = KnotGarden.Core.Reactive.ReactiveEventPhase.Parse(trigger.SignalType);
                return string.Equals(baseType, request.Type, StringComparison.OrdinalIgnoreCase);
            }).ToList();
            if (matched.Count == 0)
            {
                return Results.BadRequest(new { message = $"No wired device pin found for {kind} '{request.Type}'. Wire the pin to a node first." });
            }

            var entryNodeIds = matched.Select(trigger => trigger.EntryNodeId).Distinct(StringComparer.Ordinal).ToList();
            var targetId = matched[0].TargetId;

            var payload = System.Text.Json.JsonSerializer.SerializeToElement(request.Payload ?? new Dictionary<string, string>());
            var envelope = new KnotGarden.Core.Contracts.InboundEnvelope(
                SystemId: targetId,
                TargetId: targetId,
                Host: "simulated",
                Kind: kind,
                Type: request.Type,
                GlobalCameraNumber: null,
                ChannelId: null,
                Active: kind == KnotGarden.Core.Contracts.ExternalSignalKind.Event ? true : null,
                CorrelationKey: $"sim-{Guid.NewGuid():N}",
                Payload: payload,
                Timestamp: DateTimeOffset.UtcNow);

            var provenance = new KnotGarden.Core.Contracts.DeviceEventProvenance(
                matched[0].SourceNodeId,
                KnotGarden.Api.Services.ExternalSignalTriggerRegistry.FormatFiredPinLabel(matched[0].Kind, matched[0].SignalType));
            var executionId = await enqueuer.StartDeviceEventRunAsync(workflowId, envelope, entryNodeIds, cancellationToken, provenance);
            if (executionId is not { } execId)
            {
                return Results.Conflict(new { message = "Could not start the simulated run (no active version)." });
            }

            return Results.Accepted($"/api/executions/{execId.Value}", new { id = execId.Value, entryNodeIds });
        });

        app.MapPost("/api/workflows/{id}/schedules/{nodeId}/fire", async (string id, string nodeId, IWorkflowStore workflowStore, ExecutionStarter executionStarter, ActiveWorkflowVersionService activeWorkflowVersionService) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            var workflow = await workflowStore.GetAsync(workflowId);
            if (workflow == null)
            {
                return Results.NotFound(new { message = "Workflow definition not found" });
            }

            if (!workflow.IsEnabled)
            {
                return Results.Conflict(new { message = "Workflow is deactivated. Activate it before firing its schedules." });
            }

            var activeVersion = await activeWorkflowVersionService.GetActiveVersionAsync(workflowId);
            if (activeVersion is null)
            {
                return Results.Conflict(new { message = "Workflow has no active version. Publish and activate a version before triggering execution." });
            }

            var runtimeWorkflow = new WorkflowDefinition(workflow.Id, workflow.Name, activeVersion.Nodes, activeVersion.Edges);

            var schedulerNode = runtimeWorkflow.Nodes.FirstOrDefault(node => node.Id.Value == nodeId);
            if (schedulerNode == null)
            {
                return Results.NotFound(new { message = "Scheduler node not found" });
            }

            if (!schedulerNode.Type.Equals("scheduler", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { message = "Only scheduler nodes can be fired manually." });
            }

            var outcome = await executionStarter.StartAsync(runtimeWorkflow, activeVersion.Id, "schedule");
            if (!outcome.IsStarted)
            {
                return Results.BadRequest(new
                {
                    message = "Workflow failed compilation and cannot be triggered",
                    diagnostics = outcome.Diagnostics
                });
            }

            return Results.Accepted($"/api/executions/{outcome.Instance!.Id.Value}", outcome.Instance);
        });
    }
}
