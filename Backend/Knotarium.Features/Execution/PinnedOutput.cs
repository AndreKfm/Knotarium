// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Knotarium.Features.Execution;

/// <summary>
/// Reads a node's design-time "pinned output" property
/// (<c>__pinnedOutput = { enabled, payload, port? }</c>). A pinned node short-circuits on manual/editor
/// runs — emitting the pinned payload instead of executing — so downstream nodes can be built and
/// re-run without re-executing upstream (mirrors the inline-code "Test run" seam). It is a design/test
/// aid only: <see cref="WorkflowExecutor"/> honors pins solely on <c>manual</c>-origin runs, never on
/// active webhook/schedule/poll/signal/error runs, and publish strips the property.
/// </summary>
public static class PinnedOutput
{
    /// <summary>Node-property key carrying the pin. Editor-only; stripped on publish/export.</summary>
    public const string PropertyKey = "__pinnedOutput";

    /// <summary>Output port the pinned payload is emitted on when the pin doesn't name one.</summary>
    public const string DefaultPort = "result";

    /// <summary>
    /// When the property is present and <c>enabled</c>, returns the outputs map (port → pinned payload)
    /// to use as the node's result; otherwise null. Tolerates both a <see cref="JsonElement"/> (nested
    /// object properties are preserved as JSON) and a materialized dictionary.
    /// </summary>
    public static Dictionary<string, object>? TryReadOutputs(object? raw)
    {
        return raw switch
        {
            JsonElement element => FromJsonElement(element),
            IDictionary<string, object> dictionary => FromDictionary(dictionary),
            _ => null,
        };
    }

    private static Dictionary<string, object>? FromJsonElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!element.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        var port = element.TryGetProperty("port", out var portElement) && portElement.ValueKind == JsonValueKind.String
            ? (portElement.GetString() ?? DefaultPort)
            : DefaultPort;

        var outputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("payload", out var payload) && payload.ValueKind != JsonValueKind.Undefined)
        {
            outputs[port] = payload.Clone();
        }
        return outputs;
    }

    private static Dictionary<string, object>? FromDictionary(IDictionary<string, object> dictionary)
    {
        if (!dictionary.TryGetValue("enabled", out var enabled) || enabled is not true)
        {
            return null;
        }

        var port = dictionary.TryGetValue("port", out var portValue) && portValue is string portString && portString.Length > 0
            ? portString
            : DefaultPort;

        var outputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (dictionary.TryGetValue("payload", out var payload) && payload is not null)
        {
            outputs[port] = payload;
        }
        return outputs;
    }
}
