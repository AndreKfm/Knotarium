// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Secure fallback: reports every capability as disabled. Registered as the default so a host that wires the
/// built-in nodes without the settings-backed policy store fails closed. The real store overrides this.
/// </summary>
public sealed class DeniedCapabilityPolicy : ICapabilityPolicy
{
    public Task<bool> IsEnabledAsync(string capability, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
