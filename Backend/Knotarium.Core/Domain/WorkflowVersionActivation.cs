// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Knotarium.Core.Domain;

/// <summary>
/// Append-only record of a single activation event for a workflow definition. Unlike
/// <see cref="ActiveWorkflowVersion"/> (which is an overwriting current-state projection), this log
/// preserves the full activation timeline so the system can answer "which version was live at time T?".
/// </summary>
public sealed class WorkflowVersionActivation
{
    /// <summary>
    /// Gets or sets the activation event identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the workflow definition that was activated.
    /// </summary>
    public WorkflowDefinitionId WorkflowDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the version that became active.
    /// </summary>
    public WorkflowVersionId WorkflowVersionId { get; set; }

    /// <summary>
    /// Gets or sets the UTC time the activation took effect.
    /// </summary>
    public DateTimeOffset ActivatedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the actor that performed the activation, when known.
    /// </summary>
    public string? ActivatedBy { get; set; }

    /// <summary>
    /// Gets or sets a human-readable reason for the activation, when supplied.
    /// </summary>
    public string? ActivationReason { get; set; }

    /// <summary>
    /// Gets or sets the source version when the activation is the result of restoring an earlier version.
    /// </summary>
    public WorkflowVersionId? RestoredFromVersionId { get; set; }

    /// <summary>
    /// Gets or sets the version that was active immediately before this activation, when one existed.
    /// </summary>
    public WorkflowVersionId? PreviousActiveVersionId { get; set; }

    /// <summary>
    /// Gets or sets the correlation/request identifier tied to the activation, when available.
    /// </summary>
    public string? CorrelationId { get; set; }
}
