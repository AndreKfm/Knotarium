// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Routes the run down the branch whose label matches <c>value</c>, falling back to <c>default</c>.
///
/// <para>Branch ports are DYNAMIC: they come from the node's own <c>cases</c> property, so the manifest
/// declares no outputs at all (the same arrangement the AI Router uses). That is load-bearing in two
/// places — the compiler skips socket validation for a node with no declared outputs, and the canvas
/// derives the handles from <c>cases</c> in <c>Frontend/src/node-editor/switchPorts.ts</c>. The parsing
/// below and that file must stay in step: routing is string equality against these labels, so a
/// drifting parse draws handles that can never fire.</para>
///
/// <para>Exclusivity comes from <c>selectedPort</c>, which only takes effect because <c>switch</c> is
/// listed in <c>WorkflowExecutor.RoutesBySelectedPort</c>. Without that entry every outgoing edge would
/// fire regardless of the match and the node would branch nowhere.</para>
/// </summary>
public class SwitchNodeTask : INodeTask
{
    /// <summary>The branch taken when no case matches. Always present, always last on the canvas.</summary>
    public const string DefaultPort = "default";

    private static readonly char[] CaseSeparators = { ',', ';', '\r', '\n' };

    public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var value = GetString(context, "value");
        var port = DefaultPort;

        foreach (var candidate in ParseCases(GetString(context, "cases")))
        {
            // Case-insensitive on purpose: the labels double as UI text, and matching "Paid" against a
            // payload's "paid" is what a reader expects. Dedupe in ParseCases keeps the first spelling,
            // so two cases differing only in case cannot both be reachable.
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
            {
                port = candidate;
                break;
            }
        }

        return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object>
        {
            ["selectedPort"] = port,
            // Surfaced so a downstream node can read which branch was taken without re-deriving it,
            // and so the run inspector shows the decision rather than an empty output panel.
            ["value"] = value,
        }));
    }

    /// <summary>
    /// Ordered, case-insensitively deduplicated branch labels. Mirrors <c>parseSwitchCases</c> in
    /// switchPorts.ts.
    /// </summary>
    public static IReadOnlyList<string> ParseCases(string? raw)
    {
        var labels = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return labels;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(CaseSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(part))
            {
                labels.Add(part);
            }
        }
        return labels;
    }

    private static string GetString(NodeExecutionContext context, string name) =>
        context.Inputs.TryGetValue(name, out var raw) ? raw?.ToString() ?? string.Empty : string.Empty;
}
