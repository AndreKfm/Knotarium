using System.Collections.Generic;

namespace KnotGarden.Api;

/// <summary>
/// Represents a node package entry returned by the unified node registry endpoint.
/// </summary>
public sealed record NodePackageRegistryItem(
    string Id,
    string DisplayName,
    string Category,
    IReadOnlyList<NodePackageRegistryVersion> Versions);