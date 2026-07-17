// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;

namespace Knotarium.Features.NodeEditor;

public sealed class NodeEditorSessionGate : INodeEditorSessionGate
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _passes = new(StringComparer.OrdinalIgnoreCase);

    public void MarkPassed(string packageId, string version)
    {
        var key = BuildKey(packageId, version);
        _passes[key] = DateTimeOffset.UtcNow;
    }

    public bool HasPassingResult(string packageId, string version)
    {
        var key = BuildKey(packageId, version);
        return _passes.ContainsKey(key);
    }

    private static string BuildKey(string packageId, string version)
    {
        return $"{packageId.Trim().ToLowerInvariant()}::{version.Trim()}";
    }
}
