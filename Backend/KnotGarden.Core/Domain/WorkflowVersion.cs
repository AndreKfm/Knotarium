using System;
using System.Collections.Generic;

namespace KnotGarden.Core.Domain;

public record WorkflowVersion(
    WorkflowVersionId Id,
    WorkflowDefinitionId WorkflowDefinitionId,
    int VersionNumber,
    IReadOnlyList<NodeDefinition> Nodes,
    IReadOnlyList<EdgeDefinition> Edges,
    DateTimeOffset CreatedAt,
    WorkflowVersionOrigin Origin = WorkflowVersionOrigin.Published,
    WorkflowVersionId? SourceVersionId = null,
    string? CreatedBy = null,
    string? Label = null,
    string? CreationReason = null);
