using System;
using System.Collections.Generic;

namespace KnotGarden.Api;

/// <summary>
/// Represents a version entry for a node package returned by the unified node registry endpoint.
/// </summary>
public sealed record NodePackageRegistryVersion(
    Guid Id,
    string NodePackageId,
    string Version,
    string ManifestJson,
    string Source,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset CreatedAt);