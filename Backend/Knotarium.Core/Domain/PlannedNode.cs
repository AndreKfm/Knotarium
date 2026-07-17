// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Knotarium.Core.Domain;

public record PlannedNode(
    NodeId Id,
    string Type,
    IReadOnlyDictionary<string, object> Properties);
