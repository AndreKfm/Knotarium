// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Concatenates two collections into one array, in order: everything from <c>array1</c>, then
/// everything from <c>array2</c>.
///
/// <para>Both inputs are optional, so this doubles as a pass-through when only one is wired — useful
/// when a branch may or may not produce a second list. Omitting both yields an empty array rather than
/// an error; that is a legitimate result for "merge what arrived" and the downstream node can decide
/// whether empty is a problem.</para>
///
/// <para>A non-array input is appended as a single element rather than rejected. Merging one record
/// onto a list is a common intent, and failing it would force an artificial wrapper node.</para>
///
/// <para>The result is published under <c>success</c> to match the manifest's declared output — an edge
/// resolves its payload by looking its own output name up in this dictionary, so a mismatch here would
/// leave the wire silently empty.</para>
/// </summary>
public class MergeNodeTask : INodeTask
{
    public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var merged = new List<JsonElement>();

        foreach (var name in new[] { "array1", "array2" })
        {
            if (!context.Inputs.TryGetValue(name, out var raw) || raw is null)
            {
                continue;
            }

            try
            {
                Append(merged, raw);
            }
            catch (JsonException ex)
            {
                return Task.FromResult<LegacyNodeResult>(
                    new LegacyNodeResult.Failure($"Merge: '{name}' is not valid JSON: {ex.Message}"));
            }
        }

        return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object>
        {
            ["success"] = JsonSerializer.SerializeToElement(merged),
            // Cheap to compute and the single most common thing a following Condition node wants to
            // test, so it is surfaced rather than left to be counted downstream.
            ["count"] = merged.Count,
        }));
    }

    private static void Append(List<JsonElement> target, object raw)
    {
        // An expression-substituted field arrives as a string holding JSON text; an upstream port
        // arrives already parsed. Both have to behave the same or the node's result would depend on how
        // its input happened to be wired.
        if (raw is string text)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return;
            }
            // Both bracket forms are parsed, not just arrays: an object arriving as text (from an
            // expression-substituted field) must land as an object, exactly as it would if the same
            // value had come down a wire already parsed. Parsing only '[' would make
            // output.success[1].id work on one wiring and fail on the other.
            if (trimmed[0] == '[' || trimmed[0] == '{')
            {
                using var parsed = JsonDocument.Parse(trimmed);
                if (parsed.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in parsed.RootElement.EnumerateArray())
                    {
                        target.Add(item.Clone());
                    }
                }
                else
                {
                    target.Add(parsed.RootElement.Clone());
                }
                return;
            }
            // Anything else is a plain string value; append it as one rather than guessing.
            target.Add(JsonSerializer.SerializeToElement(text));
            return;
        }

        var element = raw as JsonElement? ?? JsonSerializer.SerializeToElement(raw);
        switch (element.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    target.Add(item.Clone());
                }
                return;
            default:
                target.Add(element.Clone());
                return;
        }
    }
}
