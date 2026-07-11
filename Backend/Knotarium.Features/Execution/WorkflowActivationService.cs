using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Features.Execution;

/// <summary>
/// Performs version-scoped activation. Unlike publish (which binds triggers from the draft being
/// published), this re-binds the live trigger registrations — schedules and polling triggers — to the
/// <em>activated</em> version's nodes, then updates the activation projection and append-only log,
/// all within a single transaction. If trigger re-binding fails the whole activation rolls back, so
/// the active pointer and the live triggers can never disagree.
/// </summary>
public sealed class WorkflowActivationService
{
    private readonly AppDbContext _dbContext;
    private readonly IReadOnlyList<IWorkflowTriggerSynchronizer> _triggerSynchronizers;
    private readonly ActiveWorkflowVersionService _activeWorkflowVersionService;

    public WorkflowActivationService(
        AppDbContext dbContext,
        IEnumerable<IWorkflowTriggerSynchronizer> triggerSynchronizers,
        ActiveWorkflowVersionService activeWorkflowVersionService)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(triggerSynchronizers);
        ArgumentNullException.ThrowIfNull(activeWorkflowVersionService);

        _dbContext = dbContext;
        _triggerSynchronizers = triggerSynchronizers.ToArray();
        _activeWorkflowVersionService = activeWorkflowVersionService;
    }

    /// <summary>
    /// Activates the supplied version: re-binds triggers to its node payload and records the
    /// activation, atomically.
    /// </summary>
    /// <returns>The persisted active-version record, or <see langword="null"/> when the version does not exist.</returns>
    /// <exception cref="DbUpdateConcurrencyException">Thrown when a concurrent activation won the race.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the activated version's triggers are invalid (e.g. bad cron).</exception>
    public async Task<ActiveWorkflowVersion?> ActivateAsync(
        WorkflowDefinitionId workflowId,
        WorkflowVersionId workflowVersionId,
        string? activatedBy = null,
        string? activationReason = null,
        WorkflowVersionId? restoredFromVersionId = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var version = await _dbContext.WorkflowVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == workflowVersionId && item.WorkflowDefinitionId == workflowId,
                cancellationToken)
            .ConfigureAwait(false);

        if (version is null)
        {
            return null;
        }

        var header = await _dbContext.WorkflowDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == workflowId, cancellationToken)
            .ConfigureAwait(false);

        var activatedDefinition = new WorkflowDefinition(
            workflowId,
            header?.Name ?? workflowId.Value,
            version.Nodes,
            version.Edges) with
        {
            IsEnabled = header?.IsEnabled ?? true
        };

        // When restore owns the outer transaction we enlist in it; otherwise we own one so the
        // trigger re-binding and the activation write commit (or roll back) together.
        var ownsTransaction = _dbContext.Database.CurrentTransaction is null;
        var transaction = ownsTransaction
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            foreach (var synchronizer in _triggerSynchronizers)
            {
                await synchronizer.SyncAsync(activatedDefinition, cancellationToken).ConfigureAwait(false);
            }

            var activated = await _activeWorkflowVersionService.ActivateAsync(
                workflowId,
                workflowVersionId,
                activatedBy,
                activationReason,
                restoredFromVersionId,
                correlationId,
                cancellationToken).ConfigureAwait(false);

            if (ownsTransaction)
            {
                await transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return activated;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
