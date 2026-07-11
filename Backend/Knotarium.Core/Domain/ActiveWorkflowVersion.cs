using System;

namespace Knotarium.Core.Domain;

/// <summary>
/// Stores the currently active runtime version for a workflow definition.
/// </summary>
public sealed class ActiveWorkflowVersion
{
    /// <summary>
    /// Gets or sets the workflow definition identifier.
    /// </summary>
    public WorkflowDefinitionId WorkflowDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the active workflow version identifier.
    /// </summary>
    public WorkflowVersionId WorkflowVersionId { get; set; }

    /// <summary>
    /// Gets or sets the UTC activation timestamp.
    /// </summary>
    public DateTimeOffset ActivatedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the actor that performed the most recent activation, when known.
    /// </summary>
    public string? ActivatedBy { get; set; }

    /// <summary>
    /// Gets or sets the optimistic-concurrency token. Rotated on every activation so that
    /// concurrent activations of the same workflow surface as a conflict instead of a lost update.
    /// </summary>
    public string ConcurrencyToken { get; set; } = Guid.NewGuid().ToString("N");
}