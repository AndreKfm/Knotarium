using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnotGarden.Infrastructure.Persistence;

// ─────────────────────────────────────────────────────────────────────────────
// The single rule for "what is a workflow's current exportable state": the active
// version, or the latest authored version when none is active, paired with the
// best available display name (live draft → stored header → id). Both the bundle
// workflow source and the folder/template exporters consume this (via the Core
// IPublishedWorkflowExportSource seam) so they can never disagree about which
// version a given workflow exports.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>EF-backed implementation of the active→latest-fallback + draft/header name rule.</summary>
public sealed class PublishedWorkflowExportSource(
    AppDbContext dbContext,
    ActiveWorkflowVersionService activeWorkflowVersionService,
    IWorkflowStore workflowStore) : IPublishedWorkflowExportSource
{
    public async Task<PublishedWorkflow?> GetAsync(
        WorkflowDefinitionId workflowId,
        CancellationToken cancellationToken = default)
    {
        // Active version is the published state; fall back to the latest authored version when none is active.
        var version = await activeWorkflowVersionService
            .GetActiveVersionAsync(workflowId, cancellationToken)
            .ConfigureAwait(false);

        version ??= await dbContext.WorkflowVersions
            .AsNoTracking()
            .Where(item => item.WorkflowDefinitionId == workflowId)
            .OrderByDescending(item => item.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (version is null)
        {
            return null;
        }

        // Prefer the live draft name, then the stored header.
        var draft = await workflowStore.GetAsync(workflowId, cancellationToken).ConfigureAwait(false);
        var header = await dbContext.WorkflowDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == workflowId, cancellationToken)
            .ConfigureAwait(false);
        var workflowName = draft?.Name ?? header?.Name ?? workflowId.Value;

        return new PublishedWorkflow(version, workflowName);
    }
}
