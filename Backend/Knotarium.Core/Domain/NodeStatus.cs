// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

namespace Knotarium.Core.Domain;

public enum NodeStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Waiting,
    RequiresManualDecision
}
