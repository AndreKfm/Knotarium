// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>A workflow's resolved published version plus the name to label its export with.</summary>
public sealed record PublishedWorkflow(WorkflowVersion Version, string DisplayName);

/// <summary>
/// Resolves a workflow id to its current exportable version + display name. The single rule for
/// "what is a workflow's current exportable state" — the active version, or the latest authored
/// version when none is active, paired with the best available display name. Feature code (bundles,
/// templates, export) depends on this seam so the EF-backed resolution can live in the persistence
/// assembly and the portability core stays Core-only.
/// </summary>
public interface IPublishedWorkflowExportSource
{
    /// <summary>Returns the published state, or <see langword="null"/> when the workflow has no version.</summary>
    Task<PublishedWorkflow?> GetAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default);
}
