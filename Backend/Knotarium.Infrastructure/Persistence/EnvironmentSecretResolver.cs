// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Infrastructure.Persistence;

public class EnvironmentSecretResolver : ISecretResolver
{
    private readonly Dictionary<string, string> _inMemorySecrets;

    public EnvironmentSecretResolver(Dictionary<string, string>? inMemorySecrets = null)
    {
        _inMemorySecrets = inMemorySecrets ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public Task<string?> ResolveAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(secretRef))
            return Task.FromResult<string?>(null);

        if (secretRef.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var envVar = secretRef.Substring(4);
            return Task.FromResult<string?>(Environment.GetEnvironmentVariable(envVar));
        }

        if (_inMemorySecrets.TryGetValue(secretRef, out var val))
        {
            return Task.FromResult<string?>(val);
        }

        return Task.FromResult<string?>(null);
    }
}
