// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Provides storage-neutral read access to workflow definitions.
/// </summary>
public interface IWorkflowStore
{
    /// <summary>
    /// Gets a workflow definition by identifier.
    /// </summary>
    /// <param name="workflowId">The workflow definition identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The workflow definition when it exists; otherwise, <see langword="null"/>.</returns>
    Task<WorkflowDefinition?> GetAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all workflow definitions visible to the current store.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The available workflow definitions.</returns>
    Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new workflow definition or updates the existing definition with the same identifier.
    /// </summary>
    /// <param name="workflow">The workflow definition to persist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The persisted workflow definition.</returns>
    Task<WorkflowDefinition> UpsertAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing workflow definition.
    /// </summary>
    /// <param name="workflow">The workflow definition to update.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated workflow definition when it exists; otherwise, <see langword="null"/>.</returns>
    Task<WorkflowDefinition?> UpdateAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an existing workflow definition.
    /// </summary>
    /// <param name="workflowId">The workflow definition identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when the workflow was deleted; otherwise, <see langword="false"/>.</returns>
    Task<bool> DeleteAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default);
}