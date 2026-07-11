using System.Collections.Generic;

namespace KnotGarden.Core.Domain;

public record PlannedNode(
    NodeId Id,
    string Type,
    IReadOnlyDictionary<string, object> Properties);
