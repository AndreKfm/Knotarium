using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Knotarium.Api.Services;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Execution;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Api;

/// <summary>
/// Execution (run) endpoints: start an external/webhook run (gated on armed runtime + enabled
/// workflow + active version), resume a suspended run by correlation token, list/read/cancel/delete
/// runs, replay from a node, discard a failed run (dead-letter triage), inspect journal / replay
/// lineage / error-run lineage, apply a manual decision, and the Condition editor's last-run value
/// lookup. The condition-values route is workflow-scoped but reads run state, so it lives here.
/// </summary>
public static class ExecutionEndpoints
{
    public static void MapExecutionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/executions", async (StartExecutionRequest request, IWorkflowStore workflowStore, ExecutionStarter executionStarter, ActiveWorkflowVersionService activeWorkflowVersionService, Knotarium.Api.Services.RuntimeArmingState armingState) =>
        {
            // Global kill-switch: external (webhook/automatic) triggers are paused while disarmed; only the
            // manual Run endpoint executes in that state.
            if (!armingState.IsArmed)
            {
                return Results.Conflict(new { message = "Runtime is disarmed. Arm the runtime to allow external triggers." });
            }

            var workflowId = new WorkflowDefinitionId(request.WorkflowDefinitionId);
            var workflow = await workflowStore.GetAsync(workflowId);
            if (workflow == null)
            {
                return Results.NotFound(new { message = "Workflow definition not found" });
            }

            if (!workflow.IsEnabled)
            {
                return Results.Conflict(new { message = "Workflow is deactivated and cannot be triggered externally." });
            }

            var activeVersion = await activeWorkflowVersionService.GetActiveVersionAsync(workflowId);
            if (activeVersion is null)
            {
                return Results.Conflict(new { message = "Workflow has no active version. Publish and activate a version before triggering execution." });
            }

            var runtimeWorkflow = new WorkflowDefinition(workflow.Id, workflow.Name, activeVersion.Nodes, activeVersion.Edges);

            var outcome = await executionStarter.StartAsync(runtimeWorkflow, activeVersion.Id, "webhook", request.InputVariables);
            if (!outcome.IsStarted)
            {
                return Results.BadRequest(new
                {
                    message = "Workflow failed compilation and cannot be triggered",
                    diagnostics = outcome.Diagnostics
                });
            }

            return Results.Accepted($"/api/executions/{outcome.Instance!.Id.Value}", outcome.Instance);
        }).AllowAnonymous();   // machine-facing webhook/external trigger — gated by the arming switch + workflow-enabled state, not a user session

        app.MapPost("/api/executions/resume", async (
            ResumeExecutionRequest request,
            HttpRequest httpRequest,
            WorkflowExecutor executor,
            CancellationToken cancellationToken) =>
        {
            var headerToken = httpRequest.Headers["X-Knotarium-Token"].FirstOrDefault();
            var token = string.IsNullOrWhiteSpace(headerToken) ? request.Token : headerToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                return Results.BadRequest(new { message = "Correlation token is required." });
            }

            var success = await executor.ResumeWorkflowTransactionAsync(token, request.Payload, cancellationToken);
            return success
                ? Results.Ok(new { message = "Workflow resume request registered." })
                : Results.BadRequest(new { message = "Failed to resume execution." });
        }).AllowAnonymous();   // machine-facing resume — authenticated by the per-run correlation token, not a user session

        app.MapGet("/api/executions", async (AppDbContext db, string? status, string? search) =>
        {
            var normalizedStatus = NormalizeExecutionStatusFilter(status);
            var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

            var query = db.ExecutionInstances
                .AsNoTracking()
                .Join(
                    db.WorkflowDefinitions.AsNoTracking(),
                    execution => execution.WorkflowDefinitionId,
                    workflow => workflow.Id,
                    (execution, workflow) => new
                    {
                        Execution = execution,
                        WorkflowName = workflow.Name
                    });

            if (normalizedStatus is not null)
            {
                query = query.Where(item => item.Execution.Status == normalizedStatus.Value);
            }

            if (normalizedSearch is not null)
            {
                query = query.Where(item => EF.Functions.Like(item.WorkflowName, $"%{normalizedSearch}%") || EF.Functions.Like(item.Execution.TriggerOrigin, $"%{normalizedSearch}%"));
            }

            var list = await query
                .OrderByDescending(item => item.Execution.CreatedAt)
                .ToListAsync();

            return Results.Ok(list.Select(item => new
            {
                id = item.Execution.Id.Value,
                workflowDefinitionId = item.Execution.WorkflowDefinitionId.Value,
                workflowVersionId = item.Execution.WorkflowVersionId != null ? item.Execution.WorkflowVersionId.Value.Value : (Guid?)null,
                status = item.Execution.Status.ToString(),
                createdAt = item.Execution.CreatedAt,
                updatedAt = item.Execution.UpdatedAt,
                triggerOrigin = item.Execution.TriggerOrigin,
                globalVariables = item.Execution.GlobalVariables,
                workflowName = item.WorkflowName
            }));
        });

        app.MapDelete("/api/executions/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var execId = new ExecutionInstanceId(id);
            var status = await db.ExecutionInstances.AsNoTracking()
                .Where(e => e.Id == execId).Select(e => (ExecutionStatus?)e.Status).FirstOrDefaultAsync();
            if (status is null) return Results.NotFound(new { message = "Execution not found" });
            if (status is ExecutionStatus.Running or ExecutionStatus.Pending)
            {
                return Results.Conflict(new { message = "Cannot delete a run that is still in progress — cancel it first." });
            }

            var deleted = await DeleteExecutionsCoreAsync(db, new[] { execId });
            return Results.Ok(new { deleted });
        });

        // Bulk delete: an explicit id set (multi-select) or all runs matching the timeline's status filter (all=true).
        app.MapPost("/api/executions/bulk-delete", async (BulkDeleteExecutionsRequest request, AppDbContext db) =>
        {
            // Never delete in-flight runs in bulk.
            IQueryable<ExecutionInstance> query = db.ExecutionInstances.AsNoTracking()
                .Where(e => e.Status != ExecutionStatus.Running && e.Status != ExecutionStatus.Pending);

            if (request.Ids is { Count: > 0 })
            {
                var ids = request.Ids.Select(g => new ExecutionInstanceId(g)).ToList();
                query = query.Where(e => ids.Contains(e.Id));
            }
            else if (request.All == true)
            {
                var normalizedStatus = NormalizeExecutionStatusFilter(request.Status);
                if (normalizedStatus is not null)
                {
                    query = query.Where(e => e.Status == normalizedStatus.Value);
                }
            }
            else
            {
                return Results.BadRequest(new { message = "Provide a non-empty 'ids' list or 'all': true." });
            }

            var targetIds = await query.Select(e => e.Id).ToListAsync();
            var deleted = await DeleteExecutionsCoreAsync(db, targetIds);
            return Results.Ok(new { deleted });
        });

        // Stop a run that's still in progress (or stuck "Running"/"Suspended" from an old crash): mark it Cancelled
        // and drop its pending work items so the worker won't resume it. Cooperative — a live executor re-reads
        // status between nodes and stops; an orphaned run is simply marked terminal so it can then be deleted.
        app.MapPost("/api/executions/{id:guid}/cancel", async (Guid id, AppDbContext db) =>
        {
            var execId = new ExecutionInstanceId(id);
            var cancelled = await db.ExecutionInstances
                .Where(e => e.Id == execId &&
                    (e.Status == ExecutionStatus.Pending || e.Status == ExecutionStatus.Running ||
                     e.Status == ExecutionStatus.Suspended || e.Status == ExecutionStatus.WaitingForRetry))
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(e => e.Status, ExecutionStatus.Cancelled)
                    .SetProperty(e => e.UpdatedAt, DateTimeOffset.UtcNow));

            if (cancelled == 0)
            {
                var exists = await db.ExecutionInstances.AsNoTracking().AnyAsync(e => e.Id == execId);
                return exists
                    ? Results.Ok(new { cancelled = false, message = "Run is already finished." })
                    : Results.NotFound(new { message = "Execution not found" });
            }

            await db.ExecutionWorkItems
                .Where(w => w.Status == WorkItemStatus.Pending && w.ExecutionInstanceId == execId)
                .ExecuteDeleteAsync();

            return Results.Ok(new { cancelled = true });
        });

        app.MapGet("/api/executions/{id}", async (Guid id, AppDbContext db) =>
        {
            var execId = new ExecutionInstanceId(id);
            var instance = await db.ExecutionInstances
                .Include(e => e.NodeStates)
                .FirstOrDefaultAsync(e => e.Id == execId);

            return instance != null ? Results.Ok(instance) : Results.NotFound();
        });

        // Latest run (with per-node states) for a workflow — powers the editor-side per-node I/O inspector,
        // so selecting a node on the design canvas can show its most recent inputs/outputs. 204 = no runs yet.
        app.MapGet("/api/workflows/{id}/latest-execution", async (string id, AppDbContext db) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            var instance = await db.ExecutionInstances
                .Include(e => e.NodeStates)
                .Where(e => e.WorkflowDefinitionId == workflowId)
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefaultAsync();

            return instance != null ? Results.Ok(instance) : Results.NoContent();
        });

        // Condition editor "Last run" value source (Phase 5): resolve the given operand refs against this
        // workflow's most recent run WITHOUT re-executing. Reuses the runtime resolver over a read-only
        // projection of the stored run, so the editor shows the value the workflow actually produced. When
        // there is no run yet, returns an empty value map and the editor falls back to manual samples.
        app.MapPost("/api/workflows/{id}/condition-values", async (string id, ConditionValuesRequest request, AppDbContext db) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            var run = await db.ExecutionInstances
                .AsNoTracking()
                .Include(e => e.NodeStates)
                .Where(e => e.WorkflowDefinitionId == workflowId)
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefaultAsync();

            if (run is null)
            {
                return Results.Ok(new
                {
                    runId = (Guid?)null,
                    versionId = (Guid?)null,
                    createdAt = (DateTimeOffset?)null,
                    stale = false,
                    values = new Dictionary<string, object>(),
                });
            }

            // Staleness: the last run was produced by a version other than the one currently active.
            var active = await db.ActiveWorkflowVersions.AsNoTracking()
                .FirstOrDefaultAsync(a => a.WorkflowDefinitionId == workflowId);
            var stale = run.WorkflowVersionId is { } runVersion && active is not null && active.WorkflowVersionId != runVersion;

            var resolved = Knotarium.Features.Nodes.Condition.ConditionLastRunResolver.Resolve(
                run, request.Refs ?? Array.Empty<string>());
            var values = resolved.ToDictionary(
                kv => kv.Key,
                kv => (object)new { found = kv.Value.Found, value = kv.Value.Value, sensitive = kv.Value.Sensitive });

            return Results.Ok(new
            {
                runId = (Guid?)run.Id.Value,
                versionId = run.WorkflowVersionId?.Value,
                createdAt = (DateTimeOffset?)run.CreatedAt,
                stale,
                values,
            });
        });

        app.MapGet("/api/executions/{id}/journal", async (Guid id, AppDbContext db) =>
        {
            var execId = new ExecutionInstanceId(id);
            var journal = await db.JournalEntries
                .Where(j => j.ExecutionInstanceId == execId)
                .OrderBy(j => j.Timestamp)
                .ToListAsync();

            return Results.Ok(journal);
        });

        app.MapPost("/api/executions/{id}/replay", async (
            Guid id,
            ReplayExecutionRequest request,
            ReplayService replayService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.FromNodeId))
            {
                return Results.BadRequest(new { message = "fromNodeId is required." });
            }

            try
            {
                var result = await replayService.CreateReplayAsync(
                    new ExecutionInstanceId(id),
                    NodeId.Create(request.FromNodeId),
                    request.TargetVersionId.HasValue ? new WorkflowVersionId(request.TargetVersionId.Value) : null,
                    request.MockSideEffects ?? false,
                    cancellationToken);

                if (result is null)
                {
                    return Results.NotFound(new { message = "Source execution not found." });
                }

                return Results.Accepted(
                    $"/api/executions/{result.NewExecutionId.Value}",
                    new
                    {
                        newExecutionId = result.NewExecutionId.Value,
                        warnings = result.Warnings.Select(warning => new
                        {
                            nodeId = warning.NodeId,
                            sideEffectKind = warning.SideEffectKind
                        })
                    });
            }
            catch (ReplayValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        // Dead-letter triage: mark a failed run as discarded so it drops out of the actionable list.
        app.MapPost("/api/executions/{id}/discard", async (
            Guid id,
            AppDbContext db,
            IExecutionJournalWriter journalWriter,
            CancellationToken cancellationToken) =>
        {
            var execId = new ExecutionInstanceId(id);
            var instance = await db.ExecutionInstances.FirstOrDefaultAsync(e => e.Id == execId, cancellationToken);
            if (instance is null)
            {
                return Results.NotFound();
            }

            if (!ExecutionDiscardPolicy.CanDiscard(instance.Status))
            {
                return Results.Conflict(new { message = "Only failed executions can be discarded." });
            }

            instance.Status = ExecutionStatus.Discarded;
            instance.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            await journalWriter.WriteAsync(new ExecutionJournal
            {
                Id = Guid.NewGuid(),
                ExecutionInstanceId = execId,
                NodeId = null,
                Timestamp = DateTimeOffset.UtcNow,
                EventType = "ExecutionDiscarded",
                Message = "Execution discarded from the dead-letter view."
            });

            return Results.Ok(new { id, status = instance.Status.ToString() });
        });

        app.MapGet("/api/executions/{id}/replays", async (Guid id, AppDbContext db) =>
        {
            var execId = new ExecutionInstanceId(id);
            var replays = await db.ExecutionInstances
                .AsNoTracking()
                .Where(execution => execution.ReplayOfExecutionId == execId)
                .OrderBy(execution => execution.CreatedAt)
                .ToListAsync();

            return Results.Ok(replays.Select(execution => new
            {
                id = execution.Id.Value,
                status = execution.Status.ToString(),
                createdAt = execution.CreatedAt,
                updatedAt = execution.UpdatedAt,
                triggerOrigin = execution.TriggerOrigin,
                replayOfExecutionId = execution.ReplayOfExecutionId!.Value.Value,
                replayFromNodeId = execution.ReplayFromNodeId!.Value.Value
            }));
        });

        // Forward lineage: the error-handler run started for a failed run (204 when none).
        app.MapGet("/api/executions/{id}/error-run", async (Guid id, AppDbContext db) =>
        {
            var execId = new ExecutionInstanceId(id);
            var errorRun = await db.ExecutionInstances
                .AsNoTracking()
                .Where(execution => execution.ErrorOfExecutionId == execId)
                .OrderByDescending(execution => execution.CreatedAt)
                .FirstOrDefaultAsync();

            if (errorRun is null)
            {
                return Results.NoContent();
            }

            return Results.Ok(new
            {
                id = errorRun.Id.Value,
                workflowDefinitionId = errorRun.WorkflowDefinitionId.Value,
                status = errorRun.Status.ToString(),
                createdAt = errorRun.CreatedAt,
                updatedAt = errorRun.UpdatedAt,
                triggerOrigin = errorRun.TriggerOrigin
            });
        });

        app.MapPost("/api/executions/{id}/nodes/{nodeId}/manual-decision", async (
            Guid id,
            string nodeId,
            ManualDecisionRequest request,
            WorkflowExecutor executor,
            CancellationToken cancellationToken) =>
        {
            var success = await executor.ApplyManualDecisionAsync(
                id,
                nodeId,
                request.Decision,
                request.Reason,
                request.ExpectedAttemptId,
                cancellationToken);

            return success
                ? Results.Ok(new { message = "Manual decision recorded successfully." })
                : Results.BadRequest(new { message = "Failed to apply manual decision." });
        });
    }

    private static ExecutionStatus? NormalizeExecutionStatusFilter(string? status)
    {
        if (string.IsNullOrWhiteSpace(status) || string.Equals(status, "All", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(status, "Retrying", StringComparison.OrdinalIgnoreCase))
        {
            return ExecutionStatus.WaitingForRetry;
        }

        if (Enum.TryParse<ExecutionStatus>(status, true, out var parsedStatus))
        {
            return parsedStatus;
        }

        return null;
    }

    // Delete executions (records in the Operations Timeline) along with their journal / node-state / work-item
    // rows. In-flight runs (Running/Pending) are never deleted — cancel them first (deactivate the workflow) so
    // a live executor isn't pulled out from under it.
    private static async Task<int> DeleteExecutionsCoreAsync(AppDbContext db, IReadOnlyList<ExecutionInstanceId> ids)
    {
        if (ids.Count == 0) return 0;
        var idList = ids.ToList();
        await db.NodeStates.Where(x => idList.Contains(x.ExecutionInstanceId)).ExecuteDeleteAsync();
        await db.JournalEntries.Where(x => idList.Contains(x.ExecutionInstanceId)).ExecuteDeleteAsync();
        await db.ExecutionWorkItems.Where(x => idList.Contains(x.ExecutionInstanceId)).ExecuteDeleteAsync();
        await db.NodeRetryStates.Where(x => idList.Contains(x.ExecutionInstanceId)).ExecuteDeleteAsync();
        await db.CorrelationTokens.Where(x => idList.Contains(x.ExecutionInstanceId)).ExecuteDeleteAsync();
        return await db.ExecutionInstances.Where(x => idList.Contains(x.Id)).ExecuteDeleteAsync();
    }
}
