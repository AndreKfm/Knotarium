// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Infrastructure.Persistence;

/// <summary>
/// Reads workflow definitions from the runtime database without exposing persistence details to callers.
/// </summary>
public class DatabaseWorkflowStore : IWorkflowStore, IWorkflowDefinitionProvider
{
    private readonly AppDbContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseWorkflowStore"/> class.
    /// </summary>
    /// <param name="context">The application database context.</param>
    public DatabaseWorkflowStore(AppDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <summary>
    /// Gets a workflow definition by identifier.
    /// </summary>
    /// <param name="workflowId">The workflow definition identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The workflow definition when it exists; otherwise, <see langword="null"/>.</returns>
    public async Task<WorkflowDefinition?> GetAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default)
    {
        return await _context.WorkflowDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(workflow => workflow.Id == workflowId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lists all workflow definitions visible to the current store.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The available workflow definitions.</returns>
    public async Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await _context.WorkflowDefinitions
            .AsNoTracking()
            .OrderBy(workflow => workflow.Name)
            .ThenBy(workflow => workflow.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new workflow definition or updates the existing definition with the same identifier.
    /// </summary>
    /// <param name="workflow">The workflow definition to persist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The persisted workflow definition.</returns>
    public async Task<WorkflowDefinition> UpsertAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var existing = await _context.WorkflowDefinitions
            .FirstOrDefaultAsync(item => item.Id == workflow.Id, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _context.WorkflowDefinitions.Add(workflow);
        }
        else
        {
            _context.Entry(existing).CurrentValues.SetValues(workflow);
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return workflow;
    }

    /// <summary>
    /// Updates an existing workflow definition.
    /// </summary>
    /// <param name="workflow">The workflow definition to update.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated workflow definition when it exists; otherwise, <see langword="null"/>.</returns>
    public async Task<WorkflowDefinition?> UpdateAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var existing = await _context.WorkflowDefinitions
            .FirstOrDefaultAsync(item => item.Id == workflow.Id, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return null;
        }

        _context.Entry(existing).CurrentValues.SetValues(workflow);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return workflow;
    }

    /// <summary>
    /// Deletes an existing workflow definition.
    /// </summary>
    /// <param name="workflowId">The workflow definition identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when the workflow was deleted; otherwise, <see langword="false"/>.</returns>
    public async Task<bool> DeleteAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default)
    {
        var existing = await _context.WorkflowDefinitions
            .FirstOrDefaultAsync(item => item.Id == workflowId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return false;
        }

        _context.WorkflowDefinitions.Remove(existing);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Gets a workflow definition by identifier for compiler/runtime consumers.
    /// </summary>
    /// <param name="id">The workflow definition identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The workflow definition when it exists; otherwise, <see langword="null"/>.</returns>
    public Task<WorkflowDefinition?> GetDefinitionAsync(WorkflowDefinitionId id, CancellationToken cancellationToken = default)
    {
        return GetAsync(id, cancellationToken);
    }
}