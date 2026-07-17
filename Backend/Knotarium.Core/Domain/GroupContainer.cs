// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Knotarium.Core.Domain;

/// <summary>
/// A centralized configuration container containing version metadata and groups.
/// </summary>
public record GroupContainer(int Version, IReadOnlyList<GroupDefinition> Groups);
