// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

namespace Knotarium.Core.Domain;

public record PlannedEdge(
    string Id,
    NodeId From,
    string Output,
    NodeId To,
    string Input);
