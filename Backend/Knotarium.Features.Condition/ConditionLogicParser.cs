// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Knotarium.Features.Condition;

/// <summary>
/// Parses + schema-validates the persisted <c>logic</c> blob into typed <see cref="ConditionLogic"/>
/// records. Rejects malformed structures rather than silently accepting them (the FIX list): wrong
/// version, bad combinator, empty/oversized comparator list, duplicate ids, unknown operator ids,
/// invalid kind/type, mistyped literals, over-length literal/ref/regex. Every violation is an
/// <see cref="ConditionErrorCode.INVALID_LOGIC"/> — schema validation + the publish gate should keep
/// these from runtime, but if one slips through it fails loud (never routes to <c>false</c>).
/// </summary>
public static class ConditionLogicParser
{
    public const int MaxComparators = 50;     // v1 flat cap / max children per group (B10)
    public const int MaxGroupChildren = 50;   // B10 — mirrors MaxComparators
    public const int MaxTreeDepth = 20;        // B10 — root = depth 1
    public const int MaxTreeNodes = 200;       // B10 — total cmp + group + not nodes
    public const int MaxLiteralLength = 10_000;
    public const int MaxRefLength = 2_000;
    public const int MaxRegexLength = 512; // mirrors ConditionEvaluator

    // Tracks cross-tree state during a v2 parse (B10): every node id must be unique across the WHOLE
    // tree, and the total node count is bounded.
    private sealed class ParseContext
    {
        public readonly HashSet<string> SeenIds = new(StringComparer.Ordinal);
        public int NodeCount;
    }

    public static bool TryParse(object? raw, out ConditionLogic? logic, out ConditionError? error)
    {
        logic = null;
        error = null;

        JsonElement root;
        try
        {
            root = ToElement(raw);
        }
        catch (Exception ex)
        {
            error = Invalid($"logic is not valid JSON: {ex.Message}");
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = Invalid("logic must be an object.");
            return false;
        }

        // version in {1, 2}. v1 (flat) is migrated to a v2 tree in memory (spec §10.4).
        if (!root.TryGetProperty("version", out var versionEl) ||
            versionEl.ValueKind != JsonValueKind.Number ||
            !versionEl.TryGetInt32(out var version) || (version != 1 && version != 2))
        {
            error = Invalid("logic.version must be 1 or 2.");
            return false;
        }

        return version == 1
            ? TryParseV1(root, out logic, out error)
            : TryParseV2(root, out logic, out error);
    }

    // ── v1 (flat) → normalize to a v2 tree (spec §10.4) ──
    private static bool TryParseV1(JsonElement root, out ConditionLogic? logic, out ConditionError? error)
    {
        logic = null;
        error = null;

        var combStr = root.TryGetProperty("comb", out var combEl) && combEl.ValueKind == JsonValueKind.String
            ? combEl.GetString()
            : null;
        Combinator comb;
        if (string.Equals(combStr, "and", StringComparison.Ordinal)) comb = Combinator.And;
        else if (string.Equals(combStr, "or", StringComparison.Ordinal)) comb = Combinator.Or;
        else { error = Invalid("logic.comb must be 'and' or 'or'."); return false; }

        if (!root.TryGetProperty("cmps", out var cmpsEl) || cmpsEl.ValueKind != JsonValueKind.Array)
        {
            error = Invalid("logic.cmps must be an array.");
            return false;
        }
        int count = cmpsEl.GetArrayLength();
        if (count < 1) { error = Invalid("logic.cmps must contain at least one comparator."); return false; }
        if (count > MaxComparators) { error = Invalid($"logic.cmps exceeds {MaxComparators} comparators."); return false; }

        var leaves = new List<LogicNode>(count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cmpEl in cmpsEl.EnumerateArray())
        {
            if (!TryParseComparator(cmpEl, seenIds, out var cmp, out error)) return false;
            leaves.Add(cmp!);
        }

        // A single comparator wires straight through (bare root); otherwise a root group folds them.
        LogicNode rootNode = leaves.Count == 1 ? leaves[0] : new GroupNode("root", comb, leaves);
        logic = new ConditionLogic(2, rootNode);
        return true;
    }

    // ── v2 (tree) — recursive parse + B10 bounds ──
    private static bool TryParseV2(JsonElement root, out ConditionLogic? logic, out ConditionError? error)
    {
        logic = null;
        if (!root.TryGetProperty("root", out var rootEl))
        {
            error = Invalid("logic.root is required for version 2.");
            return false;
        }
        var ctx = new ParseContext();
        if (!TryParseNode(rootEl, ctx, depth: 1, out var node, out error)) return false;
        logic = new ConditionLogic(2, node!);
        return true;
    }

    private static bool TryParseNode(JsonElement el, ParseContext ctx, int depth, out LogicNode? node, out ConditionError? error)
    {
        node = null;
        error = null;

        if (depth > MaxTreeDepth) { error = Invalid($"logic tree exceeds max depth {MaxTreeDepth}."); return false; }
        if (++ctx.NodeCount > MaxTreeNodes) { error = Invalid($"logic tree exceeds {MaxTreeNodes} nodes."); return false; }

        if (el.ValueKind != JsonValueKind.Object) { error = Invalid("each logic node must be an object."); return false; }

        var kind = el.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String ? kindEl.GetString() : null;
        switch (kind)
        {
            case "cmp":
                if (!TryParseComparator(el, ctx.SeenIds, out var cmp, out error)) return false;
                node = cmp;
                return true;

            case "group":
            {
                if (!TryReadNodeId(el, ctx, out var id, out error)) return false;
                var opStr = el.TryGetProperty("op", out var opEl) && opEl.ValueKind == JsonValueKind.String ? opEl.GetString() : null;
                Combinator op;
                if (string.Equals(opStr, "and", StringComparison.Ordinal)) op = Combinator.And;
                else if (string.Equals(opStr, "or", StringComparison.Ordinal)) op = Combinator.Or;
                else { error = WithId(Invalid($"group '{id}' op must be 'and' or 'or'."), id); return false; }

                if (!el.TryGetProperty("children", out var childrenEl) || childrenEl.ValueKind != JsonValueKind.Array)
                {
                    error = WithId(Invalid($"group '{id}' children must be an array."), id);
                    return false;
                }
                int childCount = childrenEl.GetArrayLength();
                if (childCount < 1) { error = WithId(Invalid($"group '{id}' must have at least one child."), id); return false; }
                if (childCount > MaxGroupChildren) { error = WithId(Invalid($"group '{id}' exceeds {MaxGroupChildren} children."), id); return false; }

                var children = new List<LogicNode>(childCount);
                foreach (var childEl in childrenEl.EnumerateArray())
                {
                    if (!TryParseNode(childEl, ctx, depth + 1, out var child, out error)) return false;
                    children.Add(child!);
                }
                node = new GroupNode(id!, op, children);
                return true;
            }

            case "not":
            {
                if (!TryReadNodeId(el, ctx, out var id, out error)) return false;
                if (!el.TryGetProperty("child", out var childEl))
                {
                    error = WithId(Invalid($"not '{id}' requires a single 'child'."), id);
                    return false;
                }
                if (!TryParseNode(childEl, ctx, depth + 1, out var child, out error)) return false;
                node = new NotNode(id!, child!);
                return true;
            }

            default:
                error = Invalid($"logic node kind must be 'cmp', 'group', or 'not' (got '{kind}').");
                return false;
        }
    }

    // Reads + registers a group/not node id, enforcing tree-uniqueness (B10).
    private static bool TryReadNodeId(JsonElement el, ParseContext ctx, out string? id, out ConditionError? error)
    {
        error = null;
        id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) { error = Invalid("node.id is required."); return false; }
        if (!ctx.SeenIds.Add(id)) { error = Invalid($"duplicate node id '{id}'."); return false; }
        return true;
    }

    private static bool TryParseComparator(JsonElement el, HashSet<string> seenIds, out ComparatorNode? cmp, out ConditionError? error)
    {
        cmp = null;
        error = null;

        if (el.ValueKind != JsonValueKind.Object) { error = Invalid("each comparator must be an object."); return false; }

        var id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) { error = Invalid("comparator.id is required."); return false; }
        if (!seenIds.Add(id)) { error = Invalid($"duplicate comparator id '{id}'."); return false; }

        var op = el.TryGetProperty("op", out var opEl) && opEl.ValueKind == JsonValueKind.String ? opEl.GetString() : null;
        if (string.IsNullOrEmpty(op) || !ConditionOperatorCatalog.TryGet(op, out var opDef))
        {
            error = Invalid($"comparator '{id}' has unknown operator '{op}'.");
            error = error with { ComparatorId = id };
            return false;
        }

        if (!el.TryGetProperty("a", out var aEl)) { error = WithId(Invalid($"comparator '{id}' is missing operand 'a'."), id); return false; }
        if (!TryParseOperand(aEl, id!, "a", out var a, out error)) return false;

        PersistedOperand? b = null;
        if (opDef.Arity == OperatorArity.Binary)
        {
            // Binary op requires a present b slot (unary persisted b is ignored, not parsed).
            if (!el.TryGetProperty("b", out var bEl) || bEl.ValueKind == JsonValueKind.Null)
            {
                error = WithId(new ConditionError(ConditionErrorCode.INVALID_LOGIC,
                    $"comparator '{id}' operator '{op}' requires operand 'b'.", id, "b"), id);
                return false;
            }
            if (!TryParseOperand(bEl, id!, "b", out b, out error)) return false;
        }

        cmp = new ComparatorNode(id!, op!, a!, b);
        return true;
    }

    private static bool TryParseOperand(JsonElement el, string cmpId, string slot, out PersistedOperand? operand, out ConditionError? error)
    {
        operand = null;
        error = null;

        if (el.ValueKind != JsonValueKind.Object)
        {
            error = WithId(new ConditionError(ConditionErrorCode.INVALID_LOGIC, $"operand '{slot}' must be an object.", cmpId, slot), cmpId);
            return false;
        }

        var kindStr = el.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String ? kindEl.GetString() : null;
        OperandKind kind;
        if (string.Equals(kindStr, "lit", StringComparison.Ordinal)) kind = OperandKind.Lit;
        else if (string.Equals(kindStr, "ref", StringComparison.Ordinal)) kind = OperandKind.Ref;
        else { error = WithId(new ConditionError(ConditionErrorCode.INVALID_LOGIC, $"operand '{slot}' kind must be 'lit' or 'ref'.", cmpId, slot), cmpId); return false; }

        var typeStr = el.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String ? typeEl.GetString() : null;
        OperandType type;
        switch (typeStr)
        {
            case "string": type = OperandType.String; break;
            case "number": type = OperandType.Number; break;
            case "boolean": type = OperandType.Boolean; break;
            default:
                error = WithId(new ConditionError(ConditionErrorCode.INVALID_LOGIC, $"operand '{slot}' type must be string/number/boolean.", cmpId, slot), cmpId);
                return false;
        }

        if (kind == OperandKind.Lit)
        {
            if (!el.TryGetProperty("value", out var valueEl))
            {
                error = WithId(new ConditionError(ConditionErrorCode.INVALID_LOGIC, $"literal operand '{slot}' is missing 'value'.", cmpId, slot), cmpId);
                return false;
            }
            if (!TryReadTypedLiteral(valueEl, type, out var value, out var litErr))
            {
                error = WithId(new ConditionError(ConditionErrorCode.INVALID_LOGIC, $"literal operand '{slot}': {litErr}", cmpId, slot), cmpId);
                return false;
            }
            operand = new PersistedOperand(OperandKind.Lit, type, value, null);
            return true;
        }

        // ref
        if (!el.TryGetProperty("ref", out var refEl) || refEl.ValueKind == JsonValueKind.Null)
        {
            error = WithId(new ConditionError(ConditionErrorCode.INVALID_LOGIC, $"reference operand '{slot}' is missing 'ref'.", cmpId, slot), cmpId);
            return false;
        }
        if (refEl.ValueKind == JsonValueKind.String && refEl.GetString()!.Length > MaxRefLength)
        {
            error = WithId(new ConditionError(ConditionErrorCode.INVALID_LOGIC, $"reference operand '{slot}' exceeds {MaxRefLength} chars.", cmpId, slot), cmpId);
            return false;
        }
        // Keep the unresolved ref spec as a cloned JsonElement; the task resolves it (D7).
        operand = new PersistedOperand(OperandKind.Ref, type, null, refEl.Clone());
        return true;
    }

    private static bool TryReadTypedLiteral(JsonElement valueEl, OperandType type, out object? value, out string? error)
    {
        value = null;
        error = null;
        switch (type)
        {
            case OperandType.String:
                if (valueEl.ValueKind != JsonValueKind.String) { error = "expected a string value."; return false; }
                var s = valueEl.GetString()!;
                if (s.Length > MaxLiteralLength) { error = $"exceeds {MaxLiteralLength} chars."; return false; }
                value = s;
                return true;
            case OperandType.Number:
                if (valueEl.ValueKind != JsonValueKind.Number) { error = "expected a number value."; return false; }
                value = valueEl.TryGetInt64(out var l) ? l : valueEl.GetDouble();
                return true;
            case OperandType.Boolean:
                if (valueEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { error = "expected a boolean value."; return false; }
                value = valueEl.GetBoolean();
                return true;
            default:
                error = "unknown type.";
                return false;
        }
    }

    private static JsonElement ToElement(object? raw)
    {
        switch (raw)
        {
            case null:
                throw new InvalidOperationException("logic is null.");
            case JsonElement el:
                return el.Clone();
            case string s:
                using (var doc = JsonDocument.Parse(s)) return doc.RootElement.Clone();
            default:
                return JsonSerializer.SerializeToElement(raw);
        }
    }

    private static ConditionError Invalid(string message) =>
        new(ConditionErrorCode.INVALID_LOGIC, message, null, null);

    private static ConditionError WithId(ConditionError e, string? id) => e with { ComparatorId = id };
}
