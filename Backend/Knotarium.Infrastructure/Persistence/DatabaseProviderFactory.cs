// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

namespace Knotarium.Infrastructure.Persistence;

public class DatabaseProviderFactory
{
    private readonly IEnumerable<IDatabaseProvider> _providers;

    public DatabaseProviderFactory(IEnumerable<IDatabaseProvider> providers)
    {
        _providers = providers;
    }

    public IDatabaseProvider GetProvider(string name)
    {
        var provider = _providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (provider == null)
        {
            throw new ArgumentException($"Unsupported database provider: {name}. Supported providers are: {string.Join(", ", _providers.Select(p => p.Name))}");
        }
        return provider;
    }
}
