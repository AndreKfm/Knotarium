using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Knotarium.Api.Services;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Knotarium.Features.Schedules;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Api;

/// <summary>
/// Workflow definition CRUD + retention lifecycle: create/update (compiles first, preserves
/// activation + metadata across editor saves), duplicate-as-draft, delete (archives when version
/// history exists), archived listing/unarchive, irreversible permanent purge (single + all), and the
/// schedule summary read.
/// </summary>
public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workflows", async (IWorkflowStore workflowStore, AppDbContext db) =>
        {
            var list = await workflowStore.ListAsync();
            var activeIds = await db.ActiveWorkflowVersions
                .Select(a => a.WorkflowDefinitionId.Value)
                .ToListAsync();

            var decorated = list.Select(w => new
            {
                id = w.Id,
                name = w.Name,
                nodes = w.Nodes,
                edges = w.Edges,
                metadata = w.Metadata,
                isEnabled = w.IsEnabled,
                hasActiveVersion = activeIds.Contains(w.Id.Value)
            });

            return Results.Ok(decorated);
        });

        app.MapGet("/api/workflows/{id}", async (string id, IWorkflowStore workflowStore) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            var workflow = await workflowStore.GetAsync(workflowId);
            return workflow != null ? Results.Ok(workflow) : Results.NotFound();
        });

        app.MapPost("/api/workflows", async (WorkflowDefinition workflow, IWorkflowStore workflowStore, WorkflowCompiler compiler, WorkflowScheduleSynchronizer scheduleSynchronizer, WorkflowPollingTriggerSynchronizer pollingSynchronizer, Knotarium.Api.Services.ExternalSignalTriggerRegistry externalSignalRegistry) =>
        {
            var compilation = await compiler.CompileAsync(workflow);
            if (!compilation.IsSuccess)
            {
                return Results.BadRequest(new
                {
                    message = "Workflow failed compilation",
                    diagnostics = compilation.Diagnostics
                });
            }

            try
            {
                var existing = await workflowStore.GetAsync(workflow.Id);
                // A canvas save sends only nodes/edges/name — it doesn't carry the group/alert Metadata or the
                // enabled flag. Preserve those from the existing record (an incoming non-null Metadata still wins),
                // otherwise every save would wipe the workflow's dashboard group + failure-alert config. Mirrors PUT.
                var workflowToSave = existing is null
                    ? workflow
                    : workflow with { IsEnabled = existing.IsEnabled, Metadata = workflow.Metadata ?? existing.Metadata };
                var persistedWorkflow = await workflowStore.UpsertAsync(workflowToSave);
                await scheduleSynchronizer.SyncAsync(persistedWorkflow);
                await pollingSynchronizer.SyncAsync(persistedWorkflow);
                await externalSignalRegistry.SyncAsync(persistedWorkflow);

                if (existing != null)
                {
                    return Results.Ok(persistedWorkflow);
                }

                return Results.Created($"/api/workflows/{workflow.Id.Value}", persistedWorkflow);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        // Clone a whole workflow into a brand-new draft: same graph (nodes/edges) + group/alert metadata, a fresh
        // id, and a "(copy)" name. The copy starts DISABLED with no published version — it's an editable draft the
        // user reviews and publishes when ready, so it never runs or steals the original's runtime activation.
        app.MapPost("/api/workflows/{id}/duplicate", async (string id, IWorkflowStore workflowStore) =>
        {
            var source = await workflowStore.GetAsync(new WorkflowDefinitionId(id));
            if (source is null)
            {
                return Results.NotFound(new { message = "Workflow definition not found" });
            }

            var copy = new WorkflowDefinition(
                WorkflowDefinitionId.New(),
                $"{source.Name} (copy)",
                source.Nodes,
                source.Edges,
                source.Metadata)
            {
                IsEnabled = false,
            };

            var saved = await workflowStore.UpsertAsync(copy);
            return Results.Created($"/api/workflows/{saved.Id.Value}", saved);
        });

        app.MapPut("/api/workflows/{id}", async (string id, WorkflowDefinition workflow, IWorkflowStore workflowStore, WorkflowCompiler compiler, WorkflowScheduleSynchronizer scheduleSynchronizer, WorkflowPollingTriggerSynchronizer pollingSynchronizer, Knotarium.Api.Services.ExternalSignalTriggerRegistry externalSignalRegistry) =>
        {
            if (id != workflow.Id.Value)
            {
                return Results.BadRequest(new { message = "Path ID and body ID mismatch" });
            }

            var compilation = await compiler.CompileAsync(workflow);
            if (!compilation.IsSuccess)
            {
                return Results.BadRequest(new
                {
                    message = "Workflow failed compilation",
                    diagnostics = compilation.Diagnostics
                });
            }

            try
            {
                // The editor saves only the graph (nodes/edges/name); activation and metadata (group membership +
                // failure-alert routing) are owned by dedicated endpoints. Preserve both from the stored workflow
                // so an editor save (which omits them) doesn't silently reset IsEnabled to its default or drop the
                // workflow's group/alert assignment. An incoming non-null Metadata still wins (future-proofing).
                var existing = await workflowStore.GetAsync(workflow.Id);
                var workflowToSave = existing is null
                    ? workflow
                    : workflow with { IsEnabled = existing.IsEnabled, Metadata = workflow.Metadata ?? existing.Metadata };

                var updatedWorkflow = await workflowStore.UpdateAsync(workflowToSave);
                if (updatedWorkflow is null)
                {
                    return Results.NotFound(new { message = "Workflow not found" });
                }

                await scheduleSynchronizer.SyncAsync(updatedWorkflow);
                await pollingSynchronizer.SyncAsync(updatedWorkflow);
                await externalSignalRegistry.SyncAsync(updatedWorkflow);
                return Results.Ok(updatedWorkflow);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapDelete("/api/workflows/{id}", async (string id, IWorkflowStore workflowStore, AppDbContext db, Knotarium.Api.Services.ExternalSignalTriggerRegistry externalSignalRegistry) =>
        {
            var workflowId = new WorkflowDefinitionId(id);

            // Release any live inbound external-signal subscriptions for this workflow.
            await externalSignalRegistry.RemoveAsync(workflowId);

            // Retention policy: a workflow that has immutable version history is never hard-deleted. Archive
            // the DB header (preserving every version + the activation log so audit/replay survive) and remove
            // only the editable draft so it leaves the editor. History stays queryable by id.
            var hasVersions = await db.WorkflowVersions.AnyAsync(version => version.WorkflowDefinitionId == workflowId);
            if (hasVersions)
            {
                var header = await db.WorkflowDefinitions.FirstOrDefaultAsync(item => item.Id == workflowId);
                if (header is not null && !header.IsArchived)
                {
                    db.Entry(header).Property(item => item.IsArchived).CurrentValue = true;
                    await db.SaveChangesAsync();
                }

                await workflowStore.DeleteAsync(workflowId);
                return Results.Ok(new { archived = true });
            }

            var deleted = await workflowStore.DeleteAsync(workflowId);
            if (!deleted)
            {
                return Results.NotFound();
            }

            return Results.NoContent();
        });

        // Bulk-delete (archive) many workflows in one call — e.g. "undo import" after a multi-workflow import.
        // Same retention policy as the single delete: archive the header when versions exist, else drop the draft.
        app.MapPost("/api/workflows/bulk-delete", async (string[] ids, IWorkflowStore workflowStore, AppDbContext db, Knotarium.Api.Services.ExternalSignalTriggerRegistry externalSignalRegistry) =>
        {
            if (ids is null || ids.Length == 0) return Results.BadRequest(new { message = "No workflow ids provided." });

            var deleted = new List<string>();
            foreach (var raw in ids.Distinct(StringComparer.Ordinal))
            {
                var workflowId = new WorkflowDefinitionId(raw);
                await externalSignalRegistry.RemoveAsync(workflowId);

                var hasVersions = await db.WorkflowVersions.AnyAsync(version => version.WorkflowDefinitionId == workflowId);
                if (hasVersions)
                {
                    var header = await db.WorkflowDefinitions.FirstOrDefaultAsync(item => item.Id == workflowId);
                    if (header is not null && !header.IsArchived)
                    {
                        db.Entry(header).Property(item => item.IsArchived).CurrentValue = true;
                    }
                    await workflowStore.DeleteAsync(workflowId);
                    deleted.Add(raw);
                }
                else if (await workflowStore.DeleteAsync(workflowId))
                {
                    deleted.Add(raw);
                }
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = deleted.Count, ids = deleted });
        });

        // Archived (soft-deleted) workflows: the dashboard list shows only active drafts, so these are otherwise
        // unreachable. List them and offer restore (un-archive + re-materialize the draft from the latest version).
        app.MapGet("/api/workflows/archived", async (AppDbContext db) =>
        {
            var archived = await db.WorkflowDefinitions
                .Where(w => w.IsArchived)
                .Select(w => new { id = w.Id.Value, name = w.Name })
                .ToListAsync();
            return Results.Ok(archived);
        });

        app.MapPost("/api/workflows/{id}/unarchive", async (string id, IWorkflowStore workflowStore, AppDbContext db) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            var header = await db.WorkflowDefinitions.FirstOrDefaultAsync(w => w.Id == workflowId);
            if (header is null) return Results.NotFound(new { message = "No workflow with this id." });
            if (!header.IsArchived) return Results.Ok(new { id = workflowId.Value, name = header.Name, alreadyActive = true });

            var latest = await db.WorkflowVersions
                .Where(v => v.WorkflowDefinitionId == workflowId)
                .OrderByDescending(v => v.VersionNumber)
                .FirstOrDefaultAsync();
            if (latest is null) return Results.BadRequest(new { message = "No retained version to restore from." });

            // Bring the editable draft back from the latest version, then un-archive so the dashboard lists it again.
            await workflowStore.UpsertAsync(new WorkflowDefinition(workflowId, header.Name, latest.Nodes, latest.Edges));
            db.Entry(header).Property(w => w.IsArchived).CurrentValue = false;
            await db.SaveChangesAsync();
            return Results.Ok(new { id = workflowId.Value, name = header.Name, restoredFromVersion = latest.VersionNumber });
        });

        // Permanently delete an ARCHIVED workflow: purge the header and every workflow-keyed record (version
        // history, activation log, active-version pointer, schedules/polling). Irreversible — it deletes exactly
        // the history that archiving deliberately preserves, so it is gated on the workflow already being archived
        // (delete-then-permanently-delete) and leaves run/execution records alone (they are purged independently).
        app.MapDelete("/api/workflows/{id}/permanent", async (string id, IWorkflowStore workflowStore, AppDbContext db, Knotarium.Api.Services.WorkflowLifecycleService lifecycle, Knotarium.Api.Services.ExternalSignalTriggerRegistry externalSignalRegistry) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            var header = await db.WorkflowDefinitions.FirstOrDefaultAsync(w => w.Id == workflowId);
            if (header is null) return Results.NotFound(new { message = "No workflow with this id." });
            if (!header.IsArchived)
            {
                return Results.Conflict(new { message = "Only archived workflows can be permanently deleted. Delete (archive) it first." });
            }

            // Drop any live inbound subscriptions and the (usually already-removed) editable draft, then
            // atomically purge every workflow-keyed database record.
            await externalSignalRegistry.RemoveAsync(workflowId);
            await workflowStore.DeleteAsync(workflowId);
            await lifecycle.PurgeDatabaseRecordsAsync(workflowId);

            return Results.Ok(new { purged = true, id = workflowId.Value });
        });

        // Permanently delete EVERY archived workflow in one shot ("empty the trash") — same irreversible purge as
        // the per-id endpoint, applied to all currently-archived headers. Active workflows are untouched (the filter
        // is IsArchived), so this can only destroy already-deleted items.
        app.MapDelete("/api/workflows/archived/all", async (IWorkflowStore workflowStore, AppDbContext db, Knotarium.Api.Services.WorkflowLifecycleService lifecycle, Knotarium.Api.Services.ExternalSignalTriggerRegistry externalSignalRegistry) =>
        {
            var archivedIds = await db.WorkflowDefinitions.Where(w => w.IsArchived).Select(w => w.Id).ToListAsync();
            foreach (var workflowId in archivedIds)
            {
                await externalSignalRegistry.RemoveAsync(workflowId);
                await workflowStore.DeleteAsync(workflowId);
                await lifecycle.PurgeDatabaseRecordsAsync(workflowId);
            }

            return Results.Ok(new { purged = archivedIds.Count, ids = archivedIds.Select(i => i.Value).ToList() });
        });

        app.MapGet("/api/workflows/{id}/schedules", async (string id, IWorkflowStore workflowStore, AppDbContext db) =>
        {
            var workflowId = new WorkflowDefinitionId(id);
            var workflow = await workflowStore.GetAsync(workflowId);
            if (workflow == null)
            {
                return Results.NotFound(new { message = "Workflow not found" });
            }

            var schedulesById = await db.Schedules
                .Where(schedule => schedule.WorkflowDefinitionId == workflowId)
                .ToDictionaryAsync(schedule => schedule.Id);
            var now = DateTimeOffset.UtcNow;

            var scheduleSummaries = workflow.Nodes
                .Where(node => node.Type.Equals("scheduler", StringComparison.OrdinalIgnoreCase))
                .Select(node =>
                {
                    var scheduleId = WorkflowScheduleIdFactory.Create(workflowId, node.Id);
                    return schedulesById.TryGetValue(scheduleId, out var schedule)
                        ? new WorkflowScheduleSummary(
                            node.Id.Value,
                            schedule.CronExpression,
                            schedule.TimeZoneId,
                            ComputeDisplayNextFireAtUtc(schedule, now),
                            schedule.IsActive)
                        : null;
                })
                .Where(summary => summary is not null)
                .Cast<WorkflowScheduleSummary>()
                .OrderBy(summary => summary.NodeId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Ok(scheduleSummaries);

            static DateTimeOffset ComputeDisplayNextFireAtUtc(Schedule schedule, DateTimeOffset now)
            {
                if (!schedule.IsActive || schedule.NextFireAtUtc > now)
                {
                    return schedule.NextFireAtUtc;
                }

                var timeZone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
                var cronExpression = CronExpressionParser.Parse(schedule.CronExpression);
                var current = schedule.NextFireAtUtc;
                var next = cronExpression.GetNextOccurrence(current, timeZone);

                for (var catchUpCount = 0; next.HasValue && next.Value <= now && catchUpCount < 10; catchUpCount++)
                {
                    current = next.Value;
                    next = cronExpression.GetNextOccurrence(current, timeZone);
                }

                return next ?? schedule.NextFireAtUtc;
            }
        });
    }
}
