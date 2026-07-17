// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.NodeRuntime;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Sets several global variables at once from a list of name → value rows. Each row's value is
/// expression-evaluated by the executor before this runs (so values may be literals or
/// <c>{{ $node.X.output.y }}</c> / <c>{{ $variables.z }}</c>). Handy as a single initialization
/// node instead of a chain of Set Variable nodes.
/// </summary>
public sealed class SetVariablesNodeTask : INodeTask
{
    public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("variables", out var raw) || raw is null)
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success());

        // Normalize to a JSON array regardless of how it arrived (already-evaluated CLR list, or a
        // raw JsonElement in tests).
        JsonElement array;
        try
        {
            array = raw is JsonElement je ? je : JsonSerializer.SerializeToElement(raw);
        }
        catch (Exception ex)
        {
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Failure($"Set Variables: could not read rows: {ex.Message}"));
        }

        if (array.ValueKind != JsonValueKind.Array)
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success());

        var count = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;

            object? value = item.TryGetProperty("value", out var v) ? JsonToClr(v) : null;
            try
            {
                // Path-aware write so keyed names (multiple["value"], list[0]) deep-set into
                // their head container — identical behavior to the single Set Variable node.
                VariableWriter.Write(context.Variables, name!, value);
            }
            catch (VariableTreeException ex)
            {
                return Task.FromResult<LegacyNodeResult>(
                    new LegacyNodeResult.Failure($"Set Variables: '{name}': {ex.Message}"));
            }
            count++;
        }

        return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new() { ["count"] = count }));
    }

    private static object? JsonToClr(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => e.Clone(), // object/array — keep as JsonElement (GetVariable<T> can still convert it)
    };
}
