using Knotarium.Core.Domain;
using System.Collections.Immutable;

namespace Knotarium.Core.Contracts;

public sealed record ExecutionPlan(
    WorkflowDefinitionId DefinitionId,
    int Version,
    ImmutableArray<PlannedNode> Nodes,
    ImmutableArray<PlannedEdge> Edges,
    ImmutableDictionary<NodeId, ImmutableArray<NodeId>> AdjacencyList,
    ImmutableArray<NodeId> EntryNodes);
