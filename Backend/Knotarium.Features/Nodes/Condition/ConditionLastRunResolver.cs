// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.NodeRuntime;

namespace Knotarium.Features.Nodes.Condition;

/// <summary>One operand reference resolved against a stored run (Phase 5, last-run value source).</summary>
/// <param name="Found">Whether the referenced node-output/variable existed in the run (distinguishes a
/// genuine miss from a legitimate <c>null</c>, mirroring the runtime found-ness contract).</param>
/// <param name="Value">The resolved value (CLR-shaped); <c>null</c> when missing or legitimately null.</param>
/// <param name="Sensitive">Vestigial — always false since name-substring masking was removed (R5).
/// Kept for response-contract stability; re-enable via a structural typed-as-secret signal if added.</param>
public sealed record LastRunRefValue(bool Found, object? Value, bool Sensitive);

/// <summary>
/// Resolves Condition operand references (<c>{{ $node.&lt;id&gt;.output.&lt;path&gt; }}</c> /
/// <c>{{ $variables.&lt;name&gt; }}</c>) against a persisted <see cref="ExecutionInstance"/> — the editor's
/// "Last run" value source — <b>without re-executing</b>. Resolution reuses the runtime
/// <see cref="ExpressionEvaluator"/> over a read-only state projection of the stored run, so the value the
/// editor shows is the value the workflow actually produced. Found-ness is decided by whether the
/// top-level output/variable the ref names is present in the run.
///
/// Sensitive values: <see cref="ExecutionInstance"/> never persists <c>SecretValue</c>-wrapped credentials
/// (stripped at execution), so last-run values are a strict subset of what <c>GET /api/executions/{id}</c>
/// already returns to the same authenticated user — the trust boundary is unchanged, no new exposure.
/// (A previous name-substring "defense-in-depth" mask was removed: with secrets never in the run it only
/// produced false positives — masking benignly-named fields like <c>password_policy_enabled</c> while
/// missing real ones like <c>bearer</c>/<c>pat</c> — so it added noise, not safety. If a structural
/// typed-as-secret signal is ever added to the variable schema, base masking on that instead.)
/// </summary>
public static class ConditionLastRunResolver
{
    public static IReadOnlyDictionary<string, LastRunRefValue> Resolve(ExecutionInstance run, IEnumerable<string> refs)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(refs);

        var state = new LastRunWorkflowState(run);
        var result = new Dictionary<string, LastRunRefValue>(StringComparer.Ordinal);
        foreach (var raw in refs)
        {
            if (string.IsNullOrWhiteSpace(raw) || result.ContainsKey(raw)) continue;
            result[raw] = ResolveOne(raw, state);
        }
        return result;
    }

    private static LastRunRefValue ResolveOne(string refExpr, LastRunWorkflowState state)
    {
        var inner = Unwrap(refExpr);
        if (!state.Exists(inner)) return new LastRunRefValue(false, null, false);

        // The evaluator interpolates the {{ … }} form; hand it the braced expression so a whole-token
        // ref returns the typed value (a bare ref would be echoed back as a literal string).
        var braced = refExpr.Contains("{{", StringComparison.Ordinal) ? refExpr : $"{{{{ {inner} }}}}";
        return new LastRunRefValue(true, ExpressionEvaluator.Evaluate(braced, state), false);
    }

    // Strip a single leading {{ / trailing }} plus surrounding whitespace (mirrors the editor's ref form).
    private static string Unwrap(string refExpr)
    {
        var s = refExpr.Trim();
        if (s.Length >= 4 && s.StartsWith("{{", StringComparison.Ordinal) && s.EndsWith("}}", StringComparison.Ordinal))
            s = s[2..^2].Trim();
        return s;
    }

    /// <summary>Read-only <see cref="IWorkflowState"/> backed by a stored run's outputs + global variables.</summary>
    private sealed class LastRunWorkflowState : IWorkflowState
    {
        private readonly ExecutionInstance _run;

        public LastRunWorkflowState(ExecutionInstance run) => _run = run;

        public T? GetVariable<T>(string name)
        {
            if (!_run.GlobalVariables.TryGetValue(name, out var val)) return default;
            if (val is JsonElement { ValueKind: JsonValueKind.Null }) return default; // stored null → CLR null
            if (val is T typed) return typed;
            return (T?)val;
        }

        public void SetVariable(string name, object? value)
        {
            // Read-only projection of a completed run — nothing to write.
        }

        public JsonElement? GetNodeOutput(NodeId nodeId, string outputName)
        {
            var ns = FindNode(nodeId.Value);
            if (ns is null || !ns.Outputs.TryGetValue(outputName, out var val)) return null;
            return ToElement(val);
        }

        public bool TryResolveVariable(string name, out object? value) =>
            _run.GlobalVariables.TryGetValue(name, out value);

        // Found-ness: is the TOP-LEVEL output/variable the ref names present in this run? (Nested path
        // navigation that then misses still counts as "found but null", matching runtime semantics.)
        public bool Exists(string expr)
        {
            if (expr.StartsWith("$node.", StringComparison.OrdinalIgnoreCase))
            {
                var outIdx = expr.IndexOf(".output.", StringComparison.OrdinalIgnoreCase);
                if (outIdx < 0) return false;
                var nodeIdStr = expr.Substring(6, outIdx - 6);
                var outputPath = expr[(outIdx + 8)..];
                if (!VariablePath.TryParse(outputPath, out var path) || path is null) return false;
                var ns = FindNode(nodeIdStr);
                return ns is not null && ns.Outputs.ContainsKey(path.Head);
            }

            if (expr.StartsWith("$variables.", StringComparison.OrdinalIgnoreCase))
            {
                var reference = expr[11..];
                var head = VariablePath.TryParse(reference, out var vp) && vp is not null ? vp.Head : reference;
                return _run.GlobalVariables.ContainsKey(head);
            }

            return _run.GlobalVariables.ContainsKey(expr);
        }

        private NodeState? FindNode(string nodeIdValue) =>
            _run.NodeStates.FirstOrDefault(n => string.Equals(n.NodeId.Value, nodeIdValue, StringComparison.OrdinalIgnoreCase));

        private static JsonElement ToElement(object? val) =>
            val is JsonElement je ? je : JsonSerializer.SerializeToElement(val);
    }
}
