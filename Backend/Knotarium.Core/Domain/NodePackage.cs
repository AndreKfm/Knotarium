// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Knotarium.Core.Domain;

public class NodePackage
{
    public NodePackageId Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<NodePackageVersion> Versions { get; set; } = new();
}
