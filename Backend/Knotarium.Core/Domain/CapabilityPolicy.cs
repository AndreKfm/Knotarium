// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

namespace Knotarium.Core.Domain;

/// <summary>
/// Instance-global switch for privileged node capabilities that have no finer-grained policy of their own
/// (unlike the filesystem, which is governed by the <see cref="FileAccessPolicy"/>). A capability listed in
/// <see cref="EnabledCapabilities"/> is permitted; everything else is denied. Secure by default: an empty
/// list means every switchable capability (e.g. code execution, database access) is off until an admin
/// turns it on.
/// </summary>
public sealed record CapabilityPolicy(IReadOnlyList<string> EnabledCapabilities)
{
    public static CapabilityPolicy Empty { get; } = new(Array.Empty<string>());

    public bool IsEnabled(string capability) =>
        EnabledCapabilities.Any(c => string.Equals(c, capability, StringComparison.OrdinalIgnoreCase));
}
