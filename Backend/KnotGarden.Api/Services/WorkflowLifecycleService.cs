using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using KnotGarden.Core.Domain;
using KnotGarden.Infrastructure.Persistence;

namespace KnotGarden.Api.Services;

/// <summary>
/// Owns the destructive workflow-purge policy so the permanent-delete (single) and empty-the-trash
/// (all) endpoints share one implementation instead of copy-pasting it.
///
/// Every workflow-keyed table is an independent aggregate with no DB-level cascade, so each is
/// removed explicitly. The set of deletes is wrapped in a single database transaction so a failure
/// partway through can't leave a half-purged workflow (e.g. versions gone but the header stranded).
/// ScheduleFires are keyed by ScheduleId, so those ids are resolved first.
/// </summary>
public sealed class WorkflowLifecycleService(AppDbContext db)
{
    /// <summary>
    /// Atomically delete every database record keyed to <paramref name="workflowId"/>: schedule fires,
    /// schedules, polling triggers, the activation log, the active-version pointer, version history, and
    /// the definition header. Run/execution records are keyed independently and are purged elsewhere.
    /// </summary>
    public async Task PurgeDatabaseRecordsAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var scheduleIds = await db.Schedules
            .Where(s => s.WorkflowDefinitionId == workflowId)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        if (scheduleIds.Count > 0)
        {
            await db.ScheduleFires.Where(f => scheduleIds.Contains(f.ScheduleId)).ExecuteDeleteAsync(cancellationToken);
        }

        await db.Schedules.Where(s => s.WorkflowDefinitionId == workflowId).ExecuteDeleteAsync(cancellationToken);
        await db.PollingTriggers.Where(p => p.WorkflowDefinitionId == workflowId).ExecuteDeleteAsync(cancellationToken);
        await db.WorkflowVersionActivations.Where(a => a.WorkflowDefinitionId == workflowId).ExecuteDeleteAsync(cancellationToken);
        await db.ActiveWorkflowVersions.Where(a => a.WorkflowDefinitionId == workflowId).ExecuteDeleteAsync(cancellationToken);
        await db.WorkflowVersions.Where(v => v.WorkflowDefinitionId == workflowId).ExecuteDeleteAsync(cancellationToken);
        await db.WorkflowDefinitions.Where(w => w.Id == workflowId).ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }
}
