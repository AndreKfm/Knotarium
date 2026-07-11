namespace KnotGarden.Core.Domain;

public record PlannedEdge(
    string Id,
    NodeId From,
    string Output,
    NodeId To,
    string Input);
