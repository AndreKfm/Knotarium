namespace Knotarium.Core.Domain;

public record PlannedEdge(
    string Id,
    NodeId From,
    string Output,
    NodeId To,
    string Input);
