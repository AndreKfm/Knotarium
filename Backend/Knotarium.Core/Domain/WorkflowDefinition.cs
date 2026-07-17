// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Knotarium.Core.Domain;

public record NodeDefinition(
    NodeId Id, 
    string Type, 
    IReadOnlyDictionary<string, object> Properties);

public record EdgeDefinition(
    string Id, 
    NodeId From,
    string Output,
    NodeId To,
    string Input);

[method: JsonConstructor]
public record WorkflowDefinition(
    WorkflowDefinitionId Id,
    string Name,
    IReadOnlyList<NodeDefinition> Nodes,
    IReadOnlyList<EdgeDefinition> Edges,
    WorkflowMetadata? Metadata = null)
{
    /// <summary>
    /// Gets a value indicating whether the workflow is active. When <see langword="false"/>,
    /// no automatic or external trigger (schedule, manual schedule fire, webhook) starts a run
    /// and any in-flight executions are cancelled; manual runs remain available. Defaults to <see langword="true"/>.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the workflow has been archived (soft-deleted). Archiving
    /// removes the editable draft but retains the immutable version history and activation log, so
    /// the audit/replay guarantee survives deletion. Defaults to <see langword="false"/>.
    /// </summary>
    public bool IsArchived { get; init; } = false;

    /// <summary>
    /// Parameterless/alternative constructor for EF Core or backwards compatibility.
    /// </summary>
    public WorkflowDefinition(
        WorkflowDefinitionId id,
        string name,
        IReadOnlyList<NodeDefinition> nodes,
        IReadOnlyList<EdgeDefinition> edges)
        : this(id, name, nodes, edges, null)
    {
    }
}
