// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Knotarium.Features.Condition;

/// <summary>
/// The pure, type-aware Condition evaluator. The authoritative implementation of
/// docs/design/condition-operator-semantics.md (§2 precedence, §3 coercion, §5 operators, §6
/// aggregation). No I/O, no executor dependency — driven by the shared conformance fixture (B2).
/// The frontend conditionEval.ts mirrors this; the backend is authoritative on regex.
/// </summary>
public static class ConditionEvaluator
{
    /// <summary>Regex hard cap (spec §5.2 / §8).</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private const int MaxRegexLength = 512;

    public static ConditionOutcome Evaluate(ResolvedCondition condition)
    {
        var results = condition.Comparators
            .Select(c => EvaluateComparator(c))
            .ToList();
        return Aggregate(condition.Comb, results);
    }

    // ── Aggregation (spec §6) — strict: Error dominates, then Incomplete, then the boolean. ──
    public static ConditionOutcome Aggregate(Combinator comb, IReadOnlyList<ComparatorResult> results)
    {
        var firstError = results.FirstOrDefault(r => r.Status == ConditionStatus.Error);
        if (firstError is not null)
        {
            return new ConditionOutcome(ConditionStatus.Error, firstError.Error, results);
        }
        if (results.Any(r => r.Status == ConditionStatus.Incomplete))
        {
            return new ConditionOutcome(ConditionStatus.Incomplete, null, results);
        }
        bool value = comb == Combinator.And
            ? results.All(r => r.Status == ConditionStatus.True)
            : results.Any(r => r.Status == ConditionStatus.True);
        return new ConditionOutcome(value ? ConditionStatus.True : ConditionStatus.False, null, results);
    }

    // ── v2 tree evaluation (spec §10): recursive fold reusing the leaf semantics. ──
    public static ConditionOutcome EvaluateTree(ResolvedLogicNode root)
    {
        var leaves = new List<ComparatorResult>();
        var (status, error) = EvalNode(root, leaves);
        return new ConditionOutcome(status, error, leaves);
    }

    // Folds a node to its status + surfaced B7 error, appending each visited LEAF result to
    // <paramref name="leaves"/> (in depth-first order, for preview/diagnostic parity). Strict per-node
    // dominance (B9): Error → Incomplete → boolean. NOT propagates non-booleans (B8).
    private static (ConditionStatus Status, ConditionError? Error) EvalNode(
        ResolvedLogicNode node, List<ComparatorResult> leaves)
    {
        switch (node)
        {
            case ResolvedComparatorNode leaf:
            {
                var r = EvaluateComparator(leaf.Comparator);
                leaves.Add(r);
                return (r.Status, r.Error);
            }

            case ResolvedGroupNode group:
            {
                // Empty group → Incomplete (no vacuous truth; the parser also forbids it). §10.1.
                if (group.Children.Count == 0) return (ConditionStatus.Incomplete, null);

                var outcomes = new List<(ConditionStatus Status, ConditionError? Error)>(group.Children.Count);
                foreach (var child in group.Children) outcomes.Add(EvalNode(child, leaves));

                // Error dominates; surface the FIRST in child order (depth-first, since each child's
                // outcome already carries its own subtree's first error).
                foreach (var o in outcomes)
                {
                    if (o.Status == ConditionStatus.Error) return (ConditionStatus.Error, o.Error);
                }
                if (outcomes.Any(o => o.Status == ConditionStatus.Incomplete))
                {
                    return (ConditionStatus.Incomplete, null);
                }
                bool value = group.Op == Combinator.And
                    ? outcomes.All(o => o.Status == ConditionStatus.True)
                    : outcomes.Any(o => o.Status == ConditionStatus.True);
                return (value ? ConditionStatus.True : ConditionStatus.False, null);
            }

            case ResolvedNotNode not:
            {
                var (cs, ce) = EvalNode(not.Child, leaves);
                return cs switch
                {
                    ConditionStatus.True => (ConditionStatus.False, null),
                    ConditionStatus.False => (ConditionStatus.True, null),
                    ConditionStatus.Incomplete => (ConditionStatus.Incomplete, null), // propagate (B8)
                    _ => (ConditionStatus.Error, ce),                                  // propagate child error
                };
            }

            default:
                // Unreachable: the sealed hierarchy has exactly the three node kinds.
                return (ConditionStatus.Error, new ConditionError(
                    ConditionErrorCode.INTERNAL_INVARIANT, "Unknown logic node kind.", null, null));
        }
    }

    // ── Single comparator (spec §2 precedence) ──
    public static ComparatorResult EvaluateComparator(ResolvedComparator cmp)
    {
        string id = cmp.Id;

        // §2.1 — unknown operator id.
        if (!ConditionOperatorCatalog.TryGet(cmp.Op, out var op))
        {
            return ComparatorResult.Fail(id, new ConditionError(
                ConditionErrorCode.INVALID_LOGIC, $"Unknown operator '{cmp.Op}'.", id, null));
        }

        bool binary = op.Arity == OperatorArity.Binary;

        // §2.1 — arity/structure: a binary op must carry a present b slot.
        if (binary && (cmp.B is null || cmp.B.State == OperandState.Absent))
        {
            return ComparatorResult.Fail(id, new ConditionError(
                ConditionErrorCode.INVALID_LOGIC, $"Operator '{cmp.Op}' requires a second operand.", id, "b"));
        }

        // Required operands, in reporting order. Unary ops ignore b entirely (§2, §5.4/§5.5).
        var required = binary ? new[] { ("a", cmp.A), ("b", cmp.B!) } : new[] { ("a", cmp.A) };

        // §2.2 — unset → Incomplete (any required operand).
        if (required.Any(o => o.Item2.State == OperandState.Unset))
        {
            return ComparatorResult.Incomplete(id);
        }

        // §2.3 — unresolved ref → RESOLUTION_FAILED (first by operand order).
        foreach (var (slot, operand) in required)
        {
            if (operand.State == OperandState.Unresolved)
            {
                return ComparatorResult.Fail(id, new ConditionError(
                    ConditionErrorCode.RESOLUTION_FAILED, $"Operand '{slot}' could not be resolved.", id, slot));
            }
        }

        // §5 — apply the operator. Existence ops read the RAW value; everything else coerces (§3).
        return cmp.Op switch
        {
            "exists" or "nexists" or "empty" or "nempty" => Existence(id, cmp.Op, cmp.A),
            "true" or "false" => BooleanOp(id, cmp.Op, cmp.A),
            "eq" or "ne" => Equality(id, cmp.Op, cmp.A, cmp.B!),
            "gt" or "gte" or "lt" or "lte" => Ordering(id, cmp.Op, cmp.A, cmp.B!),
            "contains" or "ncontains" or "starts" or "ends" or "regex" => Text(id, cmp.Op, cmp.A, cmp.B!),
            "in" or "nin" => Membership(id, cmp.Op, cmp.A, cmp.B!),
            _ => ComparatorResult.Fail(id, new ConditionError(
                ConditionErrorCode.INVALID_LOGIC, $"Unhandled operator '{cmp.Op}'.", id, null)),
        };
    }

    // ── §5.4 Existence — read the RAW resolved value (before coercion). ──
    private static ComparatorResult Existence(string id, string op, ResolvedOperand a)
    {
        object? raw = Normalize(a.Raw);
        return op switch
        {
            "exists" => ComparatorResult.Ok(id, raw is not null),
            "nexists" => ComparatorResult.Ok(id, raw is null),
            "empty" => ComparatorResult.Ok(id, IsEmpty(raw)),
            "nempty" => ComparatorResult.Ok(id, !IsEmpty(raw)),
            _ => ComparatorResult.Fail(id, Unhandled(op, id)),
        };
    }

    private static bool IsEmpty(object? raw) => raw switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        IDictionary d => d.Count == 0,
        IEnumerable e => !e.Cast<object?>().Any(),
        _ => false, // numbers, booleans are never "empty"
    };

    // ── §5.5 Boolean — read the operand COERCED to boolean. ──
    private static ComparatorResult BooleanOp(string id, string op, ResolvedOperand a)
    {
        var (eff, err) = Coerce(a, "a", id);
        if (err is not null) return ComparatorResult.Fail(id, err);
        bool target = op == "true";
        if (eff is not bool b) return ComparatorResult.Ok(id, false); // null effective → False
        return ComparatorResult.Ok(id, b == target);
    }

    // ── §5.1 Comparison eq/ne ──
    private static ComparatorResult Equality(string id, string op, ResolvedOperand a, ResolvedOperand b)
    {
        var (ea, errA) = Coerce(a, "a", id);
        if (errA is not null) return ComparatorResult.Fail(id, errA);
        var (eb, errB) = Coerce(b, "b", id);
        if (errB is not null) return ComparatorResult.Fail(id, errB);

        bool equal = EffectiveEquals(ea, eb);
        return ComparatorResult.Ok(id, op == "eq" ? equal : !equal);
    }

    private static bool EffectiveEquals(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a is double da && b is double db) return da == db; // exact, no epsilon (R11)
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        if (a is bool ba && b is bool bb) return ba == bb;
        return false; // different effective types → defined cross-type False (§5.1)
    }

    // ── §5.1 Ordering gt/gte/lt/lte ──
    private static ComparatorResult Ordering(string id, string op, ResolvedOperand a, ResolvedOperand b)
    {
        var (ea, errA) = Coerce(a, "a", id);
        if (errA is not null) return ComparatorResult.Fail(id, errA);
        var (eb, errB) = Coerce(b, "b", id);
        if (errB is not null) return ComparatorResult.Fail(id, errB);

        // A null operand → ordering predicate unsatisfied → False (§5.1, §8).
        if (ea is null || eb is null) return ComparatorResult.Ok(id, false);

        if (ea is double da && eb is double db)
        {
            // Exact numeric comparison, no epsilon (R11): epsilon on ordering broke trichotomy (a value
            // could be both gt and lte at the boundary). gte/lte use exact >=/<= so the four ops agree.
            bool value = op switch
            {
                "gt" => da > db,
                "lt" => da < db,
                "gte" => da >= db,
                "lte" => da <= db,
                _ => false,
            };
            return ComparatorResult.Ok(id, value);
        }
        if (ea is string sa && eb is string sb)
        {
            int cmp = string.CompareOrdinal(sa, sb);
            bool value = op switch
            {
                "gt" => cmp > 0,
                "lt" => cmp < 0,
                "gte" => cmp >= 0,
                "lte" => cmp <= 0,
                _ => false,
            };
            return ComparatorResult.Ok(id, value);
        }

        // Differing, or same-but-non-orderable (e.g. both boolean), effective types → Error (§5.1).
        return ComparatorResult.Fail(id, new ConditionError(
            ConditionErrorCode.TYPE_MISMATCH,
            $"Operator '{op}' cannot order operands of differing or non-orderable types.", id, null));
    }

    // ── §5.2 Text contains/ncontains/starts/ends/regex ──
    private static ComparatorResult Text(string id, string op, ResolvedOperand a, ResolvedOperand b)
    {
        var (ea, errA) = Coerce(a, "a", id);
        if (errA is not null) return ComparatorResult.Fail(id, errA);
        var (eb, errB) = Coerce(b, "b", id);
        if (errB is not null) return ComparatorResult.Fail(id, errB);

        string? sa = ea is null ? null : StringForm(ea);
        string? sb = eb is null ? null : StringForm(eb);

        // Null operand: positive text ops → False; ncontains is the negation → True.
        if (sa is null || sb is null)
        {
            return ComparatorResult.Ok(id, op == "ncontains");
        }

        switch (op)
        {
            case "contains": return ComparatorResult.Ok(id, sa.Contains(sb, StringComparison.Ordinal));
            case "ncontains": return ComparatorResult.Ok(id, !sa.Contains(sb, StringComparison.Ordinal));
            case "starts": return ComparatorResult.Ok(id, sa.StartsWith(sb, StringComparison.Ordinal));
            case "ends": return ComparatorResult.Ok(id, sa.EndsWith(sb, StringComparison.Ordinal));
            case "regex": return RegexMatch(id, input: sa, pattern: sb);
            default: return ComparatorResult.Fail(id, Unhandled(op, id));
        }
    }

    private static ComparatorResult RegexMatch(string id, string input, string pattern)
    {
        if (pattern.Length > MaxRegexLength)
        {
            return ComparatorResult.Fail(id, new ConditionError(
                ConditionErrorCode.INVALID_LOGIC, $"Regex pattern exceeds {MaxRegexLength} characters.", id, "b"));
        }
        try
        {
            bool matched = Regex.IsMatch(input, pattern, RegexOptions.None, RegexTimeout);
            return ComparatorResult.Ok(id, matched);
        }
        catch (RegexMatchTimeoutException)
        {
            return ComparatorResult.Fail(id, new ConditionError(
                ConditionErrorCode.INVALID_LOGIC, "Regex evaluation timed out.", id, "b"));
        }
        catch (ArgumentException ex)
        {
            return ComparatorResult.Fail(id, new ConditionError(
                ConditionErrorCode.INVALID_LOGIC, $"Invalid regex pattern: {ex.Message}", id, "b"));
        }
    }

    // ── §5.3 Membership in/nin ──
    private static ComparatorResult Membership(string id, string op, ResolvedOperand a, ResolvedOperand b)
    {
        var (ea, errA) = Coerce(a, "a", id);
        if (errA is not null) return ComparatorResult.Fail(id, errA);
        var (eb, errB) = Coerce(b, "b", id);
        if (errB is not null) return ComparatorResult.Fail(id, errB);

        if (ea is null)
        {
            return ComparatorResult.Ok(id, op == "nin");
        }

        string list = eb is null ? string.Empty : StringForm(eb);
        var elements = list
            .Split(',')
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .ToList();

        bool member = elements.Any(element => ElementEquals(ea, element));
        return ComparatorResult.Ok(id, op == "in" ? member : !member);
    }

    // Compare a list element (raw text) to A using the eq-rule for A's effective type (§5.3).
    private static bool ElementEquals(object effectiveA, string element)
    {
        switch (effectiveA)
        {
            case double da:
                return TryParseInvariantDouble(element, out double de) && da == de; // exact, no epsilon (R11)
            case string sa:
                return string.Equals(sa, element, StringComparison.Ordinal);
            case bool ba:
                return TryParseBool(element, out bool be) && ba == be;
            default:
                return false;
        }
    }

    // ── §3 Coercion to the declared type (per operand, non-null raw). ──
    // Returns (effectiveValue, error). effectiveValue is double / string / bool / null.
    private static (object? Effective, ConditionError? Error) Coerce(ResolvedOperand operand, string slot, string id)
    {
        object? raw = Normalize(operand.Raw);
        if (raw is null) return (null, null); // resolved null passes through (§3 last line)

        ConditionError Fail(string message) =>
            new(ConditionErrorCode.COERCION_FAILED, message, id, slot);

        switch (operand.Type)
        {
            case OperandType.Number:
                if (TryAsDouble(raw, out double n)) return (n, null);
                if (raw is string ns)
                {
                    if (TryParseInvariantDouble(ns, out double parsed)) return (parsed, null);
                    return (null, Fail($"Operand '{slot}' value '{ns}' is not a number."));
                }
                return (null, Fail($"Operand '{slot}' cannot be coerced to number."));

            case OperandType.String:
                if (raw is string s) return (s, null);
                if (TryAsDouble(raw, out double sn)) return (NumberToString(raw, sn), null);
                if (raw is bool sbv) return (sbv ? "true" : "false", null);
                return (null, Fail($"Operand '{slot}' cannot be coerced to string."));

            case OperandType.Boolean:
                if (raw is bool bv) return (bv, null);
                if (raw is string bs)
                {
                    if (TryParseBool(bs, out bool parsedBool)) return (parsedBool, null);
                    return (null, Fail($"Operand '{slot}' value '{bs}' is not a boolean."));
                }
                return (null, Fail($"Operand '{slot}' cannot be coerced to boolean."));

            default:
                return (null, Fail($"Operand '{slot}' has an unknown declared type."));
        }
    }

    // ── Helpers ──

    private static bool TryAsDouble(object raw, out double value)
    {
        switch (raw)
        {
            case double d: value = d; return true;
            case float f: value = f; return true;
            case long l: value = l; return true;
            case int i: value = i; return true;
            case short sh: value = sh; return true;
            case byte by: value = by; return true;
            case decimal m: value = (double)m; return true;
            default: value = 0; return false;
        }
    }

    private static bool TryParseInvariantDouble(string s, out double value)
    {
        // §3¹ — leading/trailing whitespace, sign, decimal point, exponent allowed; NO thousands
        // separators; decimal separator is '.'. NaN/Infinity are rejected.
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !double.IsNaN(value) && !double.IsInfinity(value))
        {
            return true;
        }
        value = 0;
        return false;
    }

    private static bool TryParseBool(string s, out bool value)
    {
        string t = s.Trim();
        if (string.Equals(t, "true", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
        if (string.Equals(t, "false", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
        value = false;
        return false;
    }

    // §3² — number→string uses the round-trippable invariant form ("R"-equivalent).
    private static string NumberToString(object raw, double asDouble)
    {
        switch (raw)
        {
            case long l: return l.ToString(CultureInfo.InvariantCulture);
            case int i: return i.ToString(CultureInfo.InvariantCulture);
            case short sh: return sh.ToString(CultureInfo.InvariantCulture);
            case byte by: return by.ToString(CultureInfo.InvariantCulture);
            default: return asDouble.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    private static string StringForm(object effective) => effective switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(effective, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    // Normalize a raw value (possibly a JsonElement from variable resolution) into native CLR shapes
    // the operators understand: long/double/string/bool/null, List&lt;object?&gt;, Dictionary.
    private static object? Normalize(object? raw)
    {
        if (raw is not JsonElement el) return raw;
        switch (el.ValueKind)
        {
            case JsonValueKind.String: return el.GetString();
            case JsonValueKind.Number: return el.TryGetInt64(out long l) ? l : el.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null or JsonValueKind.Undefined: return null;
            case JsonValueKind.Array:
                return el.EnumerateArray().Select(x => Normalize(x)).ToList();
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var p in el.EnumerateObject()) dict[p.Name] = Normalize(p.Value);
                return dict;
            default: return null;
        }
    }

    private static ConditionError Unhandled(string op, string id) =>
        new(ConditionErrorCode.INVALID_LOGIC, $"Unhandled operator '{op}'.", id, null);
}
