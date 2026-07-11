using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Compiler;
using KnotGarden.Features.Execution;
using KnotGarden.Features.Portability;
using KnotGarden.Infrastructure.Persistence;

namespace KnotGarden.Api;

/// <summary>
/// Workflow version history + activation lifecycle: paged version/activation-history reads, the
/// point-in-time active-version query over the append-only activation log, non-persisting validate,
/// and the write paths (save version, publish, activate, restore, export, import). Publish and
/// activate share the runnability gate (unbound credential slots, incomplete conditions, blocking
/// reactive graphs).
/// </summary>
public static class WorkflowVersionEndpoints
{
    public static void MapWorkflowVersionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workflows/{id}/versions", async (string id, int? page, int? pageSize, IWorkflowStore workflowStore, ActiveWorkflowVersionService activeWorkflowVersionService, AppDbContext db) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            // History stays queryable after archive (draft removed, versions retained), so accept either.
            var workflowExists = await workflowStore.GetAsync(workflowId) is not null
                || await db.WorkflowVersions.AnyAsync(version => version.WorkflowDefinitionId == workflowId);
            if (!workflowExists)
            {
                return Results.NotFound(new { message = "Workflow definition not found" });
            }

            var pageNumber = page is > 0 ? page.Value : 1;
            var size = pageSize is > 0 and <= 200 ? pageSize.Value : 50;

            var query = db.WorkflowVersions
                .AsNoTracking()
                .Where(version => version.WorkflowDefinitionId == workflowId);

            var totalCount = await query.CountAsync();

            var versions = await query
                .OrderByDescending(version => version.VersionNumber)
                .Skip((pageNumber - 1) * size)
                .Take(size)
                .ToListAsync();

            var activeRecord = await activeWorkflowVersionService.GetActiveVersionRecordAsync(workflowId);
            var activeVersionId = activeRecord?.WorkflowVersionId.Value;

            // Per-version execution counts for the page's workflow (one grouped query, scoped to this workflow).
            var executionCounts = await db.ExecutionInstances
                .AsNoTracking()
                .Where(instance => instance.WorkflowDefinitionId == workflowId && instance.WorkflowVersionId != null)
                .GroupBy(instance => instance.WorkflowVersionId)
                .Select(group => new { group.Key, Count = group.Count() })
                .ToListAsync();

            var countByVersion = executionCounts
                .Where(entry => entry.Key.HasValue)
                .ToDictionary(entry => entry.Key!.Value.Value, entry => entry.Count);

            var items = versions
                .Select(version => new WorkflowVersionSummary(
                    version.Id.Value,
                    version.VersionNumber,
                    version.CreatedAt,
                    version.CreatedBy,
                    version.Label,
                    version.Origin.ToString(),
                    activeVersionId.HasValue && activeVersionId.Value == version.Id.Value,
                    version.SourceVersionId?.Value,
                    version.Nodes.Count,
                    countByVersion.TryGetValue(version.Id.Value, out var count) ? count : 0))
                .ToList();

            return Results.Ok(new WorkflowVersionListResponse(items, pageNumber, size, totalCount));
        });

        app.MapGet("/api/workflows/{id}/versions/{versionId:guid}", async (string id, Guid versionId, IWorkflowStore workflowStore, AppDbContext db) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            // History stays queryable after archive (draft removed, versions retained), so accept either.
            var workflowExists = await workflowStore.GetAsync(workflowId) is not null
                || await db.WorkflowVersions.AnyAsync(version => version.WorkflowDefinitionId == workflowId);
            if (!workflowExists)
            {
                return Results.NotFound(new { message = "Workflow definition not found" });
            }

            var typedVersionId = new WorkflowVersionId(versionId);
            var version = await db.WorkflowVersions
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == typedVersionId && item.WorkflowDefinitionId == workflowId);

            return version is null
                ? Results.NotFound(new { message = "Workflow version not found" })
                : Results.Ok(version);
        });

        app.MapGet("/api/workflows/{id}/active-version", async (string id, IWorkflowStore workflowStore, ActiveWorkflowVersionService activeWorkflowVersionService, AppDbContext db) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            // History stays queryable after archive (draft removed, versions retained), so accept either.
            var workflowExists = await workflowStore.GetAsync(workflowId) is not null
                || await db.WorkflowVersions.AnyAsync(version => version.WorkflowDefinitionId == workflowId);
            if (!workflowExists)
            {
                return Results.NotFound(new { message = "Workflow definition not found" });
            }

            var activeVersion = await activeWorkflowVersionService.GetActiveVersionRecordAsync(workflowId);
            return activeVersion is null ? Results.NoContent() : Results.Ok(activeVersion);
        });

        app.MapGet("/api/workflows/{id}/activation-history", async (string id, int? page, int? pageSize, AppDbContext db) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            var pageNumber = page is > 0 ? page.Value : 1;
            var size = pageSize is > 0 and <= 200 ? pageSize.Value : 50;

            var query = db.WorkflowVersionActivations
                .AsNoTracking()
                .Where(activation => activation.WorkflowDefinitionId == workflowId);

            var totalCount = await query.CountAsync();
            var rows = await query
                .OrderByDescending(activation => activation.ActivatedAtUtc)
                .Skip((pageNumber - 1) * size)
                .Take(size)
                .ToListAsync();

            var items = rows
                .Select(activation => new WorkflowActivationEvent(
                    activation.Id,
                    activation.WorkflowVersionId.Value,
                    activation.ActivatedAtUtc,
                    activation.ActivatedBy,
                    activation.ActivationReason,
                    activation.RestoredFromVersionId?.Value,
                    activation.PreviousActiveVersionId?.Value,
                    activation.CorrelationId))
                .ToList();

            return Results.Ok(new WorkflowActivationHistoryResponse(items, pageNumber, size, totalCount));
        });

        app.MapGet("/api/workflows/{id}/active-version-at", async (string id, DateTimeOffset? atUtc, AppDbContext db) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            var instant = atUtc ?? DateTimeOffset.UtcNow;

            // "What was live at time T?" — the question the append-only activation log exists to answer.
            var activation = await db.WorkflowVersionActivations
                .AsNoTracking()
                .Where(item => item.WorkflowDefinitionId == workflowId && item.ActivatedAtUtc <= instant)
                .OrderByDescending(item => item.ActivatedAtUtc)
                .FirstOrDefaultAsync();

            if (activation is null)
            {
                return Results.NoContent();
            }

            return Results.Ok(new
            {
                workflowVersionId = activation.WorkflowVersionId.Value,
                activatedAtUtc = activation.ActivatedAtUtc,
                activatedBy = activation.ActivatedBy,
                activationReason = activation.ActivationReason
            });
        });

        app.MapPost("/api/workflows/{id}/versions", async (string id, SaveVersionRequest request, WorkflowPublisher workflowPublisher) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            var version = await workflowPublisher.CreateVersionAsync(workflowId, request.Nodes, request.Edges);
            if (version is null)
            {
                return Results.NotFound(new { message = "Workflow definition not found" });
            }

            return Results.Created($"/api/workflows/{id}/versions/{version.Id.Value}", version);
        });

        // Non-persisting compile pass: returns ALL diagnostics (including non-blocking warnings such as
        // edge type mismatches) so the editor can surface them live without saving/publishing.
        app.MapPost("/api/workflows/{id}/validate", async (string id, SaveVersionRequest request, WorkflowCompiler compiler) =>
        {
            var workflow = new WorkflowDefinition(new WorkflowDefinitionId(id), id, request.Nodes, request.Edges);
            var compilation = await compiler.CompileAsync(workflow);
            // Device-block graphs don't go through the control-flow compiler, so surface their reactive
            // diagnostics (dead-end wires, untargeted blocks) here too for live editor feedback.
            var reactiveDiagnostics = KnotGarden.Core.Reactive.ReactiveGraphValidator.Validate(workflow);
            return Results.Ok(new { diagnostics = compilation.Diagnostics, reactiveDiagnostics });
        });

        app.MapPost("/api/workflows/{id}/publish", async (string id, SaveVersionRequest request, WorkflowPublisher workflowPublisher) =>
        {
            var workflowId = new WorkflowDefinitionId(id);

            // Publish gate: unbound credential slots, incomplete conditions, or wired-but-untargeted device
            // blocks all mean the workflow can't actually run. Block here with an explicit message rather than
            // letting it fail cryptically at execution time.
            var runnabilityProblem = CheckRunnable(request.Nodes, request.Edges, workflowId, id, "workflow", "publishing",
                "Open and complete them in the editor before publishing.");
            if (runnabilityProblem is not null)
            {
                return runnabilityProblem;
            }

            try
            {
                var publishResult = await workflowPublisher.PublishAsync(workflowId, request.Nodes, request.Edges);
                if (publishResult is null)
                {
                    return Results.NotFound(new { message = "Workflow definition not found" });
                }

                if (publishResult.Version is null)
                {
                    return Results.BadRequest(new
                    {
                        message = "Workflow failed compilation and cannot be published",
                        diagnostics = publishResult.Diagnostics
                    });
                }

                // Pinned nodes ride into the published version (manual runs execute the active version),
                // so a manual test run returns their pinned sample; automated webhook/schedule/poll runs
                // ignore pins. Warn — don't block — so the author knows test pins are still in place.
                var pinnedNodeIds = request.Nodes
                    .Where(node => KnotGarden.Features.Execution.PinnedOutput.TryReadOutputs(
                        node.Properties.GetValueOrDefault(KnotGarden.Features.Execution.PinnedOutput.PropertyKey)) is not null)
                    .Select(node => node.Id.Value)
                    .ToList();
                var warnings = pinnedNodeIds.Count > 0
                    ? new[]
                    {
                        $"{pinnedNodeIds.Count} node(s) have pinned test output ({string.Join(", ", pinnedNodeIds)}). " +
                        "A manual run returns the pinned sample instead of executing; automated (webhook/schedule) runs ignore pins. Clear the pins when done testing."
                    }
                    : Array.Empty<string>();

                return Results.Ok(new { workflow = publishResult.Workflow, version = publishResult.Version, warnings });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapPost("/api/workflows/{id}/activate/{versionId}", async (string id, Guid versionId, WorkflowActivationService activationService, AppDbContext db) =>
        {
            var workflowId = new WorkflowDefinitionId(id);

            // Same publish gate as above: never activate a version that still references unbound credential slots.
            var targetVersion = await db.WorkflowVersions
                .AsNoTracking()
                .FirstOrDefaultAsync(version => version.Id == new WorkflowVersionId(versionId) && version.WorkflowDefinitionId == workflowId);
            if (targetVersion is not null)
            {
                // Same runnability gate as publish: never activate a version that can't run.
                var runnabilityProblem = CheckRunnable(targetVersion.Nodes, targetVersion.Edges, workflowId, id, "version", "activating",
                    "Complete them before activating.");
                if (runnabilityProblem is not null)
                {
                    return runnabilityProblem;
                }
            }

            try
            {
                // Version-scoped activation: re-binds triggers to this version and records the activation atomically.
                var activated = await activationService.ActivateAsync(
                    workflowId,
                    new WorkflowVersionId(versionId),
                    activationReason: "Manual activation");
                if (activated is null)
                {
                    return Results.NotFound(new { message = "Workflow version not found for workflow definition" });
                }

                return Results.Ok(activated);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { message = "The active version changed concurrently. Retry the activation." });
            }
            catch (InvalidOperationException ex)
            {
                // The activated version's triggers are invalid (e.g. bad cron) — activation rolled back.
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapPost("/api/workflows/{id}/restore/{versionId}", async (string id, Guid versionId, bool? activate, WorkflowPublisher workflowPublisher) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            try
            {
                var result = await workflowPublisher.RestoreAsync(workflowId, new WorkflowVersionId(versionId), activate ?? false);
                if (result is null)
                {
                    return Results.NotFound(new { message = "Workflow version not found for workflow definition" });
                }

                if (result.Version is null)
                {
                    return Results.BadRequest(new
                    {
                        message = "Restored version failed compatibility validation and cannot be activated",
                        diagnostics = result.Diagnostics
                    });
                }

                return Results.Ok(new
                {
                    versionId = result.Version.Id.Value,
                    versionNumber = result.Version.VersionNumber,
                    origin = result.Version.Origin.ToString(),
                    restoredFromVersionId = result.Version.SourceVersionId?.Value,
                    activated = result.Activated,
                    activatedAtUtc = result.ActivatedAtUtc,
                    warnings = result.Diagnostics
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { message = "The active version changed concurrently. Retry the restore." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapPost("/api/workflows/{id}/export", async (string id, WorkflowExportService exportService) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            var result = await exportService.ExportAsync(workflowId);
            if (result is null)
            {
                return Results.NotFound(new { message = "No version available to export for this workflow" });
            }

            return Results.Ok(new { filePath = result.FilePath, versionNumber = result.VersionNumber });
        });

        app.MapPost("/api/workflows/import", async (WorkflowExportDocument document, WorkflowPublisher workflowPublisher) =>
        {
            if (document?.Manifest is null || document.Content is null || string.IsNullOrWhiteSpace(document.Manifest.WorkflowId))
            {
                return Results.BadRequest(new { message = "Invalid import document: manifest and content are required." });
            }

            var result = await workflowPublisher.ImportAsync(document);
            return Results.Ok(new
            {
                versionId = result.Version.Id.Value,
                versionNumber = result.Version.VersionNumber,
                origin = result.Version.Origin.ToString(),
                activated = false,
                warnings = result.Diagnostics
            });
        });
    }

    // Shared publish/activate runnability gate. A workflow (or version) that carries unbound credential
    // slots, incomplete condition nodes, or wired-but-untargeted device blocks cannot actually run, so it
    // is blocked with an explicit message rather than a cryptic runtime failure. Returns the first problem
    // as a 400, or null when runnable. Wording differs only by noun ("workflow"/"version"), gerund
    // ("publishing"/"activating"), and the condition remedy sentence.
    private static IResult? CheckRunnable(
        IReadOnlyList<NodeDefinition> nodes,
        IReadOnlyList<EdgeDefinition> edges,
        WorkflowDefinitionId workflowId,
        string workflowName,
        string noun,
        string gerund,
        string conditionRemedy)
    {
        var unboundSlots = KnotGarden.Features.Portability.CredentialSlotModule.FindUnboundSlots(nodes);
        if (unboundSlots.Count > 0)
        {
            return Results.BadRequest(new
            {
                message = $"This {noun} has unbound credential slot(s): {string.Join(", ", unboundSlots)}. Bind them before {gerund}.",
                unboundSlots,
            });
        }

        var incompleteConditions = KnotGarden.Features.Nodes.Condition.ConditionPublishGate.FindIncompleteConditions(nodes);
        if (incompleteConditions.Count > 0)
        {
            return Results.BadRequest(new
            {
                message = $"This {noun} has incomplete condition node(s): {string.Join(", ", incompleteConditions)}. {conditionRemedy}",
                incompleteConditions,
            });
        }

        var reactiveErrors = KnotGarden.Core.Reactive.ReactiveGraphValidator.FindBlocking(
            new WorkflowDefinition(workflowId, workflowName, nodes, edges));
        if (reactiveErrors.Count > 0)
        {
            return Results.BadRequest(new
            {
                message = $"This {noun} has misconfigured device block(s): {string.Join(", ", reactiveErrors.Select(d => d.Message))}",
                reactiveDiagnostics = reactiveErrors,
            });
        }

        return null;
    }
}
