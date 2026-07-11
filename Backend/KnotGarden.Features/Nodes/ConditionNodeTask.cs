using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Features.Condition;

namespace KnotGarden.Features.Nodes;

/// <summary>
/// Evaluates a Condition node with the type-aware engine (docs/design/condition-operator-semantics.md).
/// Precedence (FIX): valid <c>logic</c> &gt; legacy <c>left/operator/right</c> &gt; configuration error.
/// References inside <c>logic</c> are resolved <b>task-side</b> (D7) via <see cref="NodeExecutionContext.State"/>
/// so a missing ref becomes <c>RESOLUTION_FAILED</c> (fail-node), distinct from a resolved <c>null</c>.
/// Status routing: True→<c>true</c>, False→<c>false</c>, Error→<b>fail the node</b> carrying the code +
/// failing comparator/operand. Runtime <c>Incomplete</c> is structurally unreachable (see <c>Route</c>)
/// and now <b>fails loud</b> rather than routing <c>false</c>; the lone remaining <c>false</c> fallback
/// is an entirely-unconfigured node (no logic, no legacy), which the publish gate keeps from shipping.
/// </summary>
public class ConditionNodeTask : INodeTask
{
    public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        // 1. Valid logic wins.
        if (TryGetLogic(context, out var rawLogic))
        {
            if (!ConditionLogicParser.TryParse(rawLogic, out var logic, out var parseError))
            {
                return Fail(parseError!);
            }

            var resolved = ResolveNode(logic!.Root, context);
            return Route(ConditionEvaluator.EvaluateTree(resolved));
        }

        // 2. Legacy left/operator/right.
        bool hasLegacy = context.Inputs.ContainsKey("left") || context.Inputs.ContainsKey("right")
            || context.Inputs.ContainsKey("operator");
        if (hasLegacy)
        {
            context.Inputs.TryGetValue("left", out var left);
            context.Inputs.TryGetValue("right", out var right);
            var opStr = context.Inputs.TryGetValue("operator", out var opObj) ? opObj?.ToString() : null;

            if (!LegacyConditionMap.TryBuildResolved(left, opStr, right, out var legacyCond, out var legacyError))
            {
                return Fail(legacyError!); // D4: unknown legacy operator → INVALID_LOGIC fail-node.
            }
            return Route(ConditionEvaluator.Evaluate(legacyCond!));
        }

        // 3. Neither configured: an incomplete node. Falls back to false (the only Incomplete fallback);
        // the publish gate is what keeps this from shipping.
        return Port("false");
    }

    private static bool TryGetLogic(NodeExecutionContext context, out object? rawLogic)
    {
        rawLogic = null;
        if (!context.Inputs.TryGetValue("logic", out var value) || value is null)
        {
            return false;
        }
        // An empty string / whitespace logic param is treated as "not present" (fall through to legacy).
        if (value is string s && string.IsNullOrWhiteSpace(s))
        {
            return false;
        }
        rawLogic = value;
        return true;
    }

    // Turn the persisted logic TREE into a resolved tree: literals pass through; refs are resolved
    // task-side with found-ness (missing → Unresolved → RESOLUTION_FAILED at evaluation, §2.3). The
    // walk recurses through groups/nots (D7 resolution recurses — spec §10).
    private static ResolvedLogicNode ResolveNode(LogicNode node, NodeExecutionContext context) => node switch
    {
        ComparatorNode c => new ResolvedComparatorNode(new ResolvedComparator(
            c.Id, c.Op, ResolveOperand(c.A, context), c.B is null ? null : ResolveOperand(c.B, context))),
        GroupNode g => new ResolvedGroupNode(g.Op, g.Children.Select(child => ResolveNode(child, context)).ToList()),
        NotNode n => new ResolvedNotNode(ResolveNode(n.Child, context)),
        _ => throw new InvalidOperationException("Unknown logic node kind."),
    };

    private static ResolvedOperand ResolveOperand(PersistedOperand operand, NodeExecutionContext context)
    {
        if (operand.Kind == OperandKind.Lit)
        {
            return ResolvedOperand.Value(operand.Type, operand.Value);
        }

        // ref: extract a variable name or a {{ }} expression from the ref spec.
        var (variableName, expression) = ReadRefSpec(operand.Ref);

        if (variableName is not null)
        {
            if (context.State is not null)
            {
                return context.State.TryResolveVariable(variableName, out var value)
                    ? ResolvedOperand.Value(operand.Type, value)
                    : ResolvedOperand.Unresolved(operand.Type); // missing → RESOLUTION_FAILED
            }
            // No state projection (e.g. a unit test): best-effort over GlobalVariables.
            return context.GlobalVariables.TryGetValue(variableName, out var gv)
                ? ResolvedOperand.Value(operand.Type, gv)
                : ResolvedOperand.Unresolved(operand.Type);
        }

        if (expression is not null && context.State is not null)
        {
            // {{ }} expression ref: best-effort (the expression evaluator cannot report found-ness, so
            // a missing var inside an expression resolves per the evaluator, not to RESOLUTION_FAILED).
            var value = KnotGarden.NodeRuntime.ExpressionEvaluator.Evaluate(expression, context.State);
            return ResolvedOperand.Value(operand.Type, value);
        }

        return ResolvedOperand.Unresolved(operand.Type);
    }

    // A ref spec is a variable_ref object { __type, variableName }, a plain variable-name string, or a
    // "{{ … }}" expression string.
    private static (string? VariableName, string? Expression) ReadRefSpec(object? refSpec)
    {
        switch (refSpec)
        {
            case JsonElement je:
                if (je.ValueKind == JsonValueKind.Object)
                {
                    if (je.TryGetProperty("__type", out var t) && t.ValueKind == JsonValueKind.String &&
                        t.GetString() == "variable_ref" &&
                        je.TryGetProperty("variableName", out var n) && n.ValueKind == JsonValueKind.String)
                    {
                        return (NullIfEmpty(n.GetString()), null);
                    }
                    return (null, null);
                }
                if (je.ValueKind == JsonValueKind.String)
                {
                    return ClassifyString(je.GetString());
                }
                return (null, null);
            case string s:
                return ClassifyString(s);
            case IReadOnlyDictionary<string, object> dict when LooksLikeRef(dict, out var name):
                return (NullIfEmpty(name), null);
            case IDictionary<string, object> d when LooksLikeRef(d, out var name2):
                return (NullIfEmpty(name2), null);
            default:
                return (null, null);
        }
    }

    private static (string?, string?) ClassifyString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (null, null);
        return s.Contains("{{") && s.Contains("}}") ? (null, s) : (s, null);
    }

    private static bool LooksLikeRef(IEnumerable<KeyValuePair<string, object>> dict, out string? variableName)
    {
        variableName = null;
        string? type = null, name = null;
        foreach (var kvp in dict)
        {
            if (string.Equals(kvp.Key, "__type", StringComparison.OrdinalIgnoreCase)) type = kvp.Value?.ToString();
            else if (string.Equals(kvp.Key, "variableName", StringComparison.OrdinalIgnoreCase)) name = kvp.Value?.ToString();
        }
        if (type == "variable_ref") { variableName = name; return true; }
        return false;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ── Routing ──

    private static Task<LegacyNodeResult> Route(ConditionOutcome outcome)
    {
        switch (outcome.Status)
        {
            case ConditionStatus.True: return Port("true");
            case ConditionStatus.False: return Port("false");
            case ConditionStatus.Incomplete:
                // Structurally unreachable at runtime: every operand resolves to a value (Lit always;
                // Ref → value or Unresolved→RESOLUTION_FAILED; the legacy path always builds value
                // operands), so no operand is ever 'unset', and the parser requires ≥1 comparator. If
                // Incomplete still reaches here the publish/activate gate was bypassed or is stale →
                // fail loud rather than silently route false (the invariant: nothing ambiguous falls to
                // false). INVALID_LOGIC is exactly "should never reach runtime; if it does, corruption".
                return Fail(outcome.Error ?? new ConditionError(ConditionErrorCode.INTERNAL_INVARIANT,
                    "Condition evaluated to Incomplete at runtime (should have been blocked at publish).",
                    null, null));
            case ConditionStatus.Error: return Fail(outcome.Error!);
            default: return Fail(new ConditionError(ConditionErrorCode.INVALID_LOGIC, "Unknown status.", null, null));
        }
    }

    private static Task<LegacyNodeResult> Port(string port) =>
        Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object>
        {
            ["selectedPort"] = port
        }));

    // Error → fail the node, propagating code + failing comparator/operand into the failure surface
    // (FIX). The surface is a single string today, so the structured fields are encoded into it.
    private static Task<LegacyNodeResult> Fail(ConditionError error)
    {
        var where = error.ComparatorId is null
            ? string.Empty
            : $" (comparator '{error.ComparatorId}'{(error.Operand is null ? "" : $", operand '{error.Operand}'")})";
        // Keep the [CODE]-prefixed human message AND carry the code as a structured field so the audit
        // chain can field-filter on it instead of substring-matching the message (R6).
        return Task.FromResult<LegacyNodeResult>(
            new LegacyNodeResult.Failure($"[{error.Code}] {error.Message}{where}", error.Code.ToString()));
    }
}
