// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.NodeRuntime;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Extracts a value out of a JSON document with a dotted path, e.g. <c>data.items[0].id</c>.
///
/// <para>The path walk is <see cref="ExpressionEvaluator.NavigateJson"/> — the same routine the
/// declarative interpreter uses — so this node and an equivalent declarative package cannot disagree
/// about what a path means.</para>
///
/// <para>The extracted value is published under <c>success</c>, matching the output the manifest
/// declares. That is not cosmetic: an edge carries data by looking its own output name up in this
/// dictionary, so publishing under any other key would leave the wire empty while still appearing to
/// work on the canvas.</para>
///
/// <para>A missing input, a missing path, or a path that resolves to nothing FAILS the node rather than
/// returning empty. Silently passing null downstream is the harder failure to debug — the run stops
/// here with a message naming the path instead of at some later node that received nothing.</para>
/// </summary>
public class TransformNodeTask : INodeTask
{
    public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("inputJson", out var rawInput) || rawInput is null)
        {
            return Fail("Transform: 'inputJson' is required.");
        }

        var jsonPath = context.Inputs.TryGetValue("jsonPath", out var rawPath) ? rawPath?.ToString() : null;
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            return Fail("Transform: 'jsonPath' is required.");
        }

        if (!TryAsJsonElement(rawInput, out var document))
        {
            return Fail("Transform: 'inputJson' is not valid JSON.");
        }

        JsonElement? extracted;
        try
        {
            extracted = ExpressionEvaluator.NavigateJson(document, jsonPath!);
        }
        catch (Exception ex)
        {
            return Fail($"Transform: could not evaluate path '{jsonPath}': {ex.Message}");
        }

        if (extracted is null)
        {
            return Fail($"Transform: path '{jsonPath}' did not match anything in the input.");
        }

        return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object>
        {
            ["success"] = extracted.Value,
        }));
    }

    /// <summary>
    /// Normalises a node input to a JsonElement. Upstream values arrive already parsed, but an
    /// expression-substituted field arrives as a string holding JSON text, so both shapes must work. A
    /// string that is not JSON is treated as a JSON string literal rather than an error — extracting a
    /// path out of it will then fail with the clearer "did not match anything" message.
    /// </summary>
    private static bool TryAsJsonElement(object raw, out JsonElement element)
    {
        if (raw is JsonElement direct)
        {
            element = direct;
            return true;
        }

        if (raw is string text)
        {
            var trimmed = text.Trim();
            if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
            {
                try
                {
                    using var parsed = JsonDocument.Parse(trimmed);
                    element = parsed.RootElement.Clone();
                    return true;
                }
                catch (JsonException)
                {
                    element = default;
                    return false;
                }
            }
        }

        try
        {
            element = JsonSerializer.SerializeToElement(raw);
            return true;
        }
        catch (NotSupportedException)
        {
            element = default;
            return false;
        }
    }

    private static Task<LegacyNodeResult> Fail(string message) =>
        Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Failure(message));
}
