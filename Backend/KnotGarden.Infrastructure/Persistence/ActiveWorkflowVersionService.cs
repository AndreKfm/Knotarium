using System;
using KnotGarden.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnotGarden.Infrastructure.Persistence;

/// <summary>
/// Manages active runtime workflow versions.
/// </summary>
public sealed class ActiveWorkflowVersionService
{
    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActiveWorkflowVersionService"/> class.
    /// </summary>
    /// <param name="dbContext">The application database context.</param>
    /// <param name="timeProvider">The time provider used for activation timestamps.</param>
    public ActiveWorkflowVersionService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Activates an existing workflow version for the specified workflow definition.
    /// </summary>
    /// <param name="workflowId">The workflow definition identifier.</param>
    /// <param name="workflowVersionId">The workflow version identifier.</param>
    /// <param name="activatedBy">The actor performing the activation, when known.</param>
    /// <param name="activationReason">A human-readable reason for the activation, when supplied.</param>
    /// <param name="restoredFromVersionId">The source version when the activation results from a restore.</param>
    /// <param name="correlationId">The correlation/request identifier tied to the activation, when available.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The persisted active version record when the version exists; otherwise, <see langword="null"/>.</returns>
    /// <exception cref="DbUpdateConcurrencyException">
    /// Thrown when a concurrent activation changed the active version between read and write.
    /// </exception>
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
            .FirstOrDefaultAsync(
                item => item.Id == workflowVersionId && item.WorkflowDefinitionId == workflowId,
                cancellationToken)
            .ConfigureAwait(false);

        if (version is null)
        {
            return null;
        }

        var activeVersion = await _dbContext.ActiveWorkflowVersions
            .FirstOrDefaultAsync(item => item.WorkflowDefinitionId == workflowId, cancellationToken)
            .ConfigureAwait(false);

        var previousActiveVersionId = activeVersion?.WorkflowVersionId;
        var activatedAt = _timeProvider.GetUtcNow();

        if (activeVersion is null)
        {
            activeVersion = new ActiveWorkflowVersion
            {
                WorkflowDefinitionId = workflowId,
                WorkflowVersionId = workflowVersionId,
                ActivatedAtUtc = activatedAt,
                ActivatedBy = activatedBy,
                ConcurrencyToken = Guid.NewGuid().ToString("N")
            };

            _dbContext.ActiveWorkflowVersions.Add(activeVersion);
        }
        else
        {
            activeVersion.WorkflowVersionId = workflowVersionId;
            activeVersion.ActivatedAtUtc = activatedAt;
            activeVersion.ActivatedBy = activatedBy;

            // Rotate the token so a concurrent activation reading the prior value loses the update.
            activeVersion.ConcurrencyToken = Guid.NewGuid().ToString("N");
        }

        // Append-only activation log, written in the same SaveChanges (one transaction) as the projection
        // update so "what was live at time T" can never disagree with the current pointer.
        _dbContext.WorkflowVersionActivations.Add(new WorkflowVersionActivation
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            WorkflowVersionId = workflowVersionId,
            ActivatedAtUtc = activatedAt,
            ActivatedBy = activatedBy,
            ActivationReason = activationReason,
            RestoredFromVersionId = restoredFromVersionId,
            PreviousActiveVersionId = previousActiveVersionId,
            CorrelationId = correlationId
        });

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return activeVersion;
    }

    /// <summary>
    /// Gets the active runtime workflow version for the supplied workflow definition.
    /// </summary>
    /// <param name="workflowId">The workflow definition identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The active workflow version when one exists; otherwise, <see langword="null"/>.</returns>
    public async Task<WorkflowVersion?> GetActiveVersionAsync(
        WorkflowDefinitionId workflowId,
        CancellationToken cancellationToken = default)
    {
        var activeVersion = await GetActiveVersionRecordAsync(workflowId, cancellationToken).ConfigureAwait(false);

        if (activeVersion is null)
        {
            return null;
        }

        return await _dbContext.WorkflowVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == activeVersion.WorkflowVersionId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the active workflow version record for the supplied workflow definition.
    /// </summary>
    /// <param name="workflowId">The workflow definition identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The active workflow version record when one exists; otherwise, <see langword="null"/>.</returns>
    public async Task<ActiveWorkflowVersion?> GetActiveVersionRecordAsync(
        WorkflowDefinitionId workflowId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.ActiveWorkflowVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.WorkflowDefinitionId == workflowId, cancellationToken)
            .ConfigureAwait(false);
    }
}