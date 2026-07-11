using System.Collections.Generic;

namespace Knotarium.Features.Condition;

// The status model (B3) and per-operand error shape (B7). See
// docs/design/condition-operator-semantics.md — this code is the backend half of the spec; the
// frontend conditionEval.ts is the other half, and both are pinned by the shared conformance
// fixture (docs/design/condition-conformance.fixture.json).

/// <summary>Result status for a single comparator and for the aggregate (spec §1/§6).</summary>
public enum ConditionStatus
{
    True,
    False,
    Incomplete,
    Error
}

/// <summary>ErrorCode taxonomy — every value routes to the fail-node (spec §1, §7).</summary>
public enum ConditionErrorCode
{
    /// <summary>Unknown/foreign operator id, arity/structure violation, invalid/timed-out regex.</summary>
    INVALID_LOGIC,
    /// <summary>A configured reference could not be resolved at run time (distinct from resolved-null).</summary>
    RESOLUTION_FAILED,
    /// <summary>A non-null resolved value can't be brought to its OWN declared type.</summary>
    COERCION_FAILED,
    /// <summary>Ordering over two resolved, non-null operands of differing / non-orderable types.</summary>
    TYPE_MISMATCH,
    /// <summary>An internal-invariant breach: a structurally-valid condition reached an impossible
    /// runtime state (e.g. evaluated to Incomplete, which the resolution model + publish gate make
    /// unreachable). Root cause is OUR bug or a bypassed gate — NOT malformed inbound data. Kept
    /// distinct from <see cref="INVALID_LOGIC"/> so audit queries don't conflate the two.</summary>
    INTERNAL_INVARIANT
}

/// <summary>Per-operand error shape (B7): identical for runtime + Phase-6 dry-run.</summary>
public sealed record ConditionError(
    ConditionErrorCode Code,
    string Message,
    string? ComparatorId,
    string? Operand);

/// <summary>Declared operand type — persisted, non-optional (B5).</summary>
public enum OperandType
{
    String,
    Number,
    Boolean
}

/// <summary>
/// Operand state at evaluate time (spec §2). <see cref="Value"/> carries a resolved RAW value
/// (<see cref="ResolvedOperand.Raw"/>, which may legitimately be <c>null</c>); the other states model
/// the design-time / resolution outcomes that precede operator application.
/// </summary>
public enum OperandState
{
    /// <summary>A resolved value is present (possibly a legitimate <c>null</c>).</summary>
    Value,
    /// <summary>Design-time unset (empty literal draft / no ref target) → Incomplete (§2.2).</summary>
    Unset,
    /// <summary>A configured ref failed to resolve at run time → RESOLUTION_FAILED (§2.3).</summary>
    Unresolved,
    /// <summary>The operand slot is missing in persisted logic (e.g. binary op with no <c>b</c>).</summary>
    Absent
}

/// <summary>The combinator over comparator results (spec §6).</summary>
public enum Combinator
{
    And,
    Or
}

/// <summary>
/// An operand as the evaluator sees it: its declared type, its state, and (when
/// <see cref="State"/> is <see cref="OperandState.Value"/>) the resolved raw value the executor's
/// property resolution handed back — a scalar (<c>long</c>/<c>double</c>/<c>string</c>/<c>bool</c>),
/// <c>null</c>, a list (array), or a dictionary (object).
/// </summary>
public sealed record ResolvedOperand(OperandType Type, OperandState State, object? Raw)
{
    public static ResolvedOperand Value(OperandType type, object? raw) => new(type, OperandState.Value, raw);
    public static ResolvedOperand Unset(OperandType type) => new(type, OperandState.Unset, null);
    public static ResolvedOperand Unresolved(OperandType type) => new(type, OperandState.Unresolved, null);
    public static ResolvedOperand Absent(OperandType type) => new(type, OperandState.Absent, null);
}

/// <summary>One comparator ready to evaluate: its id, operator id, and resolved operands.</summary>
public sealed record ResolvedComparator(string Id, string Op, ResolvedOperand A, ResolvedOperand? B);

/// <summary>A whole flat condition ready to evaluate (legacy path + the v1 aggregation surface).</summary>
public sealed record ResolvedCondition(Combinator Comb, IReadOnlyList<ResolvedComparator> Comparators);

// ── Resolved v2 tree (Phase 8) — the persisted LogicNode tree after task-side ref resolution. ──

/// <summary>One node of the resolved boolean tree, ready for recursive evaluation (spec §10).</summary>
public abstract record ResolvedLogicNode;

/// <summary>A resolved comparator leaf — wraps the existing <see cref="ResolvedComparator"/>.</summary>
public sealed record ResolvedComparatorNode(ResolvedComparator Comparator) : ResolvedLogicNode;

/// <summary>A resolved <c>and</c>/<c>or</c> group over its resolved children.</summary>
public sealed record ResolvedGroupNode(Combinator Op, IReadOnlyList<ResolvedLogicNode> Children) : ResolvedLogicNode;

/// <summary>A resolved <c>not</c> over its single resolved child.</summary>
public sealed record ResolvedNotNode(ResolvedLogicNode Child) : ResolvedLogicNode;

/// <summary>The evaluation of a single comparator: status + (on Error) the per-operand error.</summary>
public sealed record ComparatorResult(string ComparatorId, ConditionStatus Status, ConditionError? Error)
{
    public static ComparatorResult Ok(string id, bool value) =>
        new(id, value ? ConditionStatus.True : ConditionStatus.False, null);
    public static ComparatorResult Incomplete(string id) => new(id, ConditionStatus.Incomplete, null);
    public static ComparatorResult Fail(string id, ConditionError error) => new(id, ConditionStatus.Error, error);
}

/// <summary>The aggregate outcome: status + (on Error) the first reported error (spec §6.1).</summary>
public sealed record ConditionOutcome(
    ConditionStatus Status,
    ConditionError? Error,
    IReadOnlyList<ComparatorResult> Comparators);
