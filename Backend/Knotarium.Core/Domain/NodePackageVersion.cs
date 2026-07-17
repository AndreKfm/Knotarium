// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Knotarium.Core.Domain;

public class NodePackageVersion
{
    public NodePackageVersionId Id { get; set; }
    public NodePackageId NodePackageId { get; set; }
    public string Version { get; set; } = string.Empty;
    public string ManifestJson { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public byte[]? CompiledAssembly { get; set; }
    public string? Signature { get; set; }
    public IReadOnlyList<string> Capabilities { get; set; } = Array.Empty<string>();
    public DateTimeOffset CreatedAt { get; set; }
}
