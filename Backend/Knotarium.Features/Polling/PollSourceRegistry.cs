// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Polling;

/// <summary>Resolves a registered <see cref="IPollSource"/> by its kind.</summary>
public sealed class PollSourceRegistry
{
    private readonly Dictionary<string, IPollSource> _sources;

    public PollSourceRegistry(IEnumerable<IPollSource> sources)
    {
        _sources = sources.ToDictionary(s => s.Kind, StringComparer.OrdinalIgnoreCase);
    }

    public IPollSource Resolve(string kind)
    {
        if (_sources.TryGetValue(kind, out var source))
        {
            return source;
        }

        throw new InvalidOperationException($"No poll source registered for kind '{kind}'.");
    }
}
