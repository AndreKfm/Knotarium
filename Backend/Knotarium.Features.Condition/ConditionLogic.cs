using System.Collections.Generic;

namespace Knotarium.Features.Condition;

// The PERSISTED runtime model (properties.logic) — typed, validated (B5/D3). Distinct from the
// editor's textual draft model (FE) and from the evaluator's ResolvedComparator (post ref-resolution).
// See docs/plans/condition-node-editor-TODO.md "Data model".

public enum OperandKind
{
    /// <summary>A typed literal value (persisted typed, never stringly — no runtime parsing).</summary>
    Lit,
    /// <summary>A reference into the variable system, resolved task-side at run time (D7).</summary>
    Ref
}

/// <summary>
/// A persisted operand. For <see cref="OperandKind.Lit"/>, <see cref="Value"/> carries the typed
/// literal. For <see cref="OperandKind.Ref"/>, <see cref="Ref"/> carries the unresolved reference
/// spec (a <c>variable_ref</c> object, a variable-name string, or a <c>{{ }}</c> expression string).
/// </summary>
public sealed record PersistedOperand(OperandKind Kind, OperandType Type, object? Value, object? Ref);

// ── v2 — nestable boolean tree (Phase 8, spec §10) ──────────────────────────────────────────────
// A condition is a single `root` LogicNode: a comparator leaf, an and/or group, or a not. v1 (flat
// `{comb, cmps}`) is normalized to this tree on parse (a lone comparator → bare root; otherwise a root
// group), so the model, evaluator, and task are tree-shaped while already-published v1 logic keeps
// evaluating. Leaf data + semantics are unchanged from v1.

/// <summary>One node of the persisted boolean tree. Discriminated by <c>kind</c> in JSON; each concrete
/// node carries its own tree-unique <c>Id</c> (B10).</summary>
public abstract record LogicNode;

/// <summary>A comparator leaf (<c>kind: "cmp"</c>): id, operator id, and colocated operands (D3) —
/// identical to the v1 comparator. Evaluates to a boolean status via the unchanged leaf semantics.</summary>
public sealed record ComparatorNode(string Id, string Op, PersistedOperand A, PersistedOperand? B) : LogicNode;

/// <summary>An n-ary <c>and</c>/<c>or</c> group (<c>kind: "group"</c>) over ≥1 children (B10).</summary>
public sealed record GroupNode(string Id, Combinator Op, IReadOnlyList<LogicNode> Children) : LogicNode;

/// <summary>A unary <c>not</c> (<c>kind: "not"</c>) negating exactly one child (B8).</summary>
public sealed record NotNode(string Id, LogicNode Child) : LogicNode;

/// <summary>The whole persisted condition: <c>version == 2</c> with a single <see cref="Root"/>
/// (v1 input is migrated to this in memory on parse).</summary>
public sealed record ConditionLogic(int Version, LogicNode Root);
