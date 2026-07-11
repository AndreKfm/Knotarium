using System.Collections.Generic;

namespace Knotarium.Core.Domain;

public record PlannedNode(
    NodeId Id,
    string Type,
    IReadOnlyDictionary<string, object> Properties);
