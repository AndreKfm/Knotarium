using KnotGarden.Core.Domain;
using System.Collections.Immutable;

namespace KnotGarden.Core.Contracts;

public sealed record ExecutionPlan(
    WorkflowDefinitionId DefinitionId,
    int Version,
    ImmutableArray<PlannedNode> Nodes,
    ImmutableArray<PlannedEdge> Edges,
    ImmutableDictionary<NodeId, ImmutableArray<NodeId>> AdjacencyList,
    ImmutableArray<NodeId> EntryNodes);
