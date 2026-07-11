# Condition Operator Semantics (B1)

The **single, language-neutral specification** for how the Condition engine evaluates a comparator.
Implemented **identically** in the backend runtime (`Knotarium.Features/Nodes`, Phase 1) and the
frontend live preview (`conditionEval.ts`, Phase 2), and enforced by the shared **conformance
fixture (B2)** — `docs/design/condition-conformance.fixture.json` — which both test suites load.

This spec is authoritative over the prototype's loose-JS `evalCmp`. Where they differ, **this wins**.

Companion: `condition-node-editor-TODO.md` (plan), `condition-node-editor.handoff.json` (derived).

---

## 1. Value & type model

An operand carries a **declared type** (`string | number | boolean`, persisted, non-optional — B5)
and produces a **resolved raw value** at evaluate time:
- **literal** → its typed JSON value (`value`, already `string`/`number`/`boolean`).
- **reference** → whatever `EvaluatePropertyValue` resolved the `ref` to: a JSON scalar
  (`string`/`number`/`boolean`), `null`, an **array**, an **object**, or *unresolved*.

The evaluator turns each operand into an **effective value** by **coercing the resolved raw value to
the operand's own declared type** (§3), then applies the operator (§5). Numbers are IEEE-754 doubles
throughout; all parsing/formatting is **invariant culture**.

### Status (B3)
Every comparator and the aggregate produce one of:
`True | False | Incomplete | Error`. A resolved **`null` is a legitimate value**, distinct from
`Incomplete` and `Error`.

### Error record / per-operand error shape (B7)
`{ code: ErrorCode, message: string, comparatorId: string, operand: 'a' | 'b' | null }`
`ErrorCode ∈ { INVALID_LOGIC, RESOLUTION_FAILED, COERCION_FAILED, TYPE_MISMATCH }`. The runtime evaluator and the
Phase-6 dry-run emit this **same** object so preview and runtime failures are one diagnostic type.

---

## 2. Precedence of states (evaluate a comparator in this order)

For a comparator with operator `op`, operand `a`, and (if binary) operand `b`:

1. **INVALID_LOGIC** — unknown `op` id, arity/structure violation (e.g. binary op missing the `b`
   slot in persisted logic), or a malformed operand. → `Error(INVALID_LOGIC)`. (Schema validation +
   publish gate should prevent this reaching runtime; if it does, fail loud.)
2. **Unset operand → Incomplete.** An operand is **unset** when it is absent, or a literal with an
   empty/whitespace-only textual draft, or a reference with no target chosen. If any *required*
   operand (always `a`; also `b` for binary ops) is unset → `Incomplete`. *(This is the design-time
   state; the publish gate blocks shipping it.)*
3. **RESOLUTION_FAILED → Error.** A configured reference that could not be resolved at run time
   (missing producer output, expression error). → `Error(RESOLUTION_FAILED)`. *Distinct from a
   reference that resolved to `null`* (that is a legitimate value, step 5).
4. **COERCION_FAILED → Error.** A non-null resolved value that cannot be brought to the operand's
   **own declared type** (§3). → `Error(COERCION_FAILED)`. *Not* cross-type comparison (§5.1).
5. **Apply the operator** (§5) over the effective values (which may be `null`). Operator
   application is total **except** ordering (`gt/gte/lt/lte`) over two resolved, non-null operands
   whose effective types differ or are non-orderable, which yields `Error(TYPE_MISMATCH)` (§5.1).

**Unary ops** ignore `b` entirely (a persisted `b` on a unary op is dropped, not an error).

---

## 3. Coercion to the declared type (per operand, non-null resolved value)

| Declared type | number raw | string raw | boolean raw | array/object raw |
|---|---|---|---|---|
| **number** | use as-is | invariant `double` parse¹; fail → `COERCION_FAILED` | `COERCION_FAILED` | `COERCION_FAILED` |
| **string** | invariant `"R"` round-trip text² | use as-is | `"true"`/`"false"` (lowercase) | `COERCION_FAILED`³ |
| **boolean** | `COERCION_FAILED`⁴ | `true`/`false` only, trimmed, case-insensitive; else `COERCION_FAILED` | use as-is | `COERCION_FAILED` |

¹ Parse style: leading/trailing whitespace, sign, decimal point, exponent allowed; **thousands
separators NOT allowed**; decimal separator is `.` (invariant). `NaN`/`Infinity` literals → fail.
² Number→string uses the round-trippable invariant form (e.g. `1.5`, `1000`, `1E-05` as .NET `"R"`/
JS equivalent — both sides MUST match; pin exact expectations in the fixture).
³ Arrays/objects are only meaningful to the **existence** ops, which read the *raw* value before
coercion (§5.4). For all other ops a declared-scalar operand receiving an array/object →
`COERCION_FAILED`.
⁴ Numbers do **not** coerce to boolean (no `0`/`1` truthiness) — keep it strict and surprise-free.

A resolved **`null`** is **never** a coercion failure — it passes through as the effective value
`null` (handled per-op in §5).

---

## 4. Type-aware operator availability (editor) vs runtime

The catalog's `accepts` drives **editor filtering** (a `number` left operand hides text ops). It is a
UX guardrail, **not** a runtime guarantee — runtime still evaluates whatever was persisted by §5.
The catalog (`id, arity, accepts`) lives in the **catalog fixture (B2)** and is asserted identical in
both languages (drift test).

Because ordering `accepts` includes **`string`** (so the editor offers lexical ordering — `§5.1`
defines it at runtime, and hiding it would leave defined-but-unreachable behavior), two editor
obligations follow:
- **Ordinal-ordering hint:** when an ordering op (`gt/gte/lt/lte`) targets a **string** operand, the
  editor surfaces a hint that the comparison is **lexical/ordinal**, not numeric (`"9" > "10"` is
  `False`; `"Z" < "a"` is `True`). Cheap, and it heads off a class of silent wrong-branch bugs.
- **Edit-time cross-type block:** when both operand effective types are known at edit time and differ
  for an ordering op (e.g. `number` vs `string`), the editor blocks/flags it (never persisted). The
  runtime `TYPE_MISMATCH` rule (§5.1) is the backstop for the dynamic-ref case the editor can't see.

---

## 5. Operator semantics

Notation: `A` = effective value of `a`, `B` = effective value of `b` (post-coercion). "comparable
type" = both `A` and `B` are non-null and of the **same** effective kind (both number or both string).

### 5.1 Comparison — `eq ne gt gte lt lte` (binary)

- **`eq`**: `True` iff `A` and `B` are **equal within the same type**:
  - both number → **exact** IEEE-double equality (`A == B`), **no epsilon (R11)**. Epsilon on both `eq`
    and ordering broke trichotomy (a near-boundary value was simultaneously `eq` and `gt`); since the type
    system has one `number` kind and literals persist typed, exact equality is predictable and consistent.
    Derived floats that drift (e.g. `0.1 + 0.2`) won't be `eq` — the standard FP caveat; model with a range.
  - both string → **ordinal** equality (case-sensitive, culture-invariant).
  - both boolean → identity.
  - both `null` → `True`.
  - **different effective types, or exactly one `null`** → `False` (defined cross-type semantics —
    a legitimate `False`, never an Error).
- **`ne`**: logical negation of `eq` (so cross-type → `True`, `null` vs non-null → `True`).
- **`gt gte lt lte`**: require a **comparable type**:
  - both number → **exact** arithmetic compare, **no epsilon (R11)**: `gte` ⇒ `A >= B`, `lte` ⇒ `A <= B`.
    Keeping the four ops exact preserves trichotomy (no value is both `gt` and `lte` at a boundary).
  - both string → **ordinal** compare (`String.CompareOrdinal` / code-unit order); `gte`/`lte`
    include ordinal equality.
  - **a `null` operand** → **`False`** (the ordering predicate is unsatisfied by absence; not an
    Error — symmetric with the comparison/text/membership null handling).
  - **two resolved, non-null operands whose effective types differ** (e.g. `number` vs `string`), **or
    are the same but non-orderable** (e.g. both `boolean`) → **`Error(TYPE_MISMATCH)`** → fail-node
    (`operand: null` — the conflict is the pair, not one side). Ordering has no defined cross-type
    answer; routing a genuinely-incomparable pair to `false` is exactly the silent-wrong-branch hazard
    D1/D2 forbid. This is reachable only via **dynamic refs** whose runtime types differ — when both
    operand types are known at **edit time** and differ, the editor's type-aware guardrail (§4) blocks
    the comparator before it can persist. (Unlike `eq`/`ne`, whose cross-type answer *is* defined as a
    legitimate `False`/`True` — §5.1 above.)

### 5.2 Text — `contains ncontains starts ends regex` (binary)

Operate on the **string forms** of `A` and `B` (coerced to string per §3; both operands' declared
type is typically `string`, and `accepts` restricts these ops to `string`/`array`/`any`).
- If `A` or `B` is `null` → `contains`/`starts`/`ends`/`regex` → `False`; `ncontains` → `True`
  (negation of `contains`).
- **`contains`** = `A.Contains(B)` **ordinal, case-sensitive**. **`ncontains`** = negation.
- **`starts`** = `A.StartsWith(B)` ordinal; **`ends`** = `A.EndsWith(B)` ordinal. Empty `B` → `True`
  (every string starts/ends with `""`), matching ordinal `StartsWith("")`.
- **`regex`**: `B` is the pattern, `A` the input. **Flavor:** .NET `System.Text.RegularExpressions`
  on the backend; the FE preview uses JS `RegExp` — **document the dialect gap**: only the common
  subset is guaranteed identical; the **backend is authoritative**, and the fixture pins patterns
  from the common subset only. **Options:** none by default (case-sensitive, no multiline/singleline)
  — case-insensitivity must be expressed in-pattern (`(?i)`). **Timeout:** 100 ms hard cap
  (`Regex` matchTimeout) → on timeout `Error(COERCION_FAILED)`? No — a runaway pattern is invalid
  configuration → **`Error(INVALID_LOGIC)`**. **Invalid pattern** (compile error) →
  `Error(INVALID_LOGIC)`. **Max pattern length** 512 chars (schema-validated; over → `INVALID_LOGIC`).

### 5.3 Membership — `in nin` (binary)

`B` is a **comma-separated list**; `A` is the candidate. (`accepts`: `string`/`number`.)
- **Parsing of `B`** (when `B`'s effective/string form is the list): split on commas, **trim each
  element**, **drop empty elements** (so `"a,,b"` = `{a,b}`, trailing comma ignored). **No quoting /
  escaping in v1** — a comma always splits (documented limitation; revisit later). Each element is
  then compared to `A` using the **`eq` rule for `A`'s effective type** (numeric elements parsed
  invariantly when `A` is a number; string-ordinal when `A` is a string).
- **`in`** = `True` iff any element equals `A`. **`nin`** = negation.
- `A` is `null` → `in` → `False`, `nin` → `True`. `B` empty list → `in` → `False`, `nin` → `True`.

### 5.4 Existence — `empty nempty exists nexists` (unary; read the RAW resolved value)

These inspect the **raw resolved value** (before scalar coercion), because they're defined over
absence/emptiness across kinds:
- **`exists`** = `True` iff the value is **non-null** (resolved and not JSON null). **`nexists`** =
  value is `null`. *(A `RESOLUTION_FAILED` reference is still an `Error` per §2.3 — `exists` does not
  rescue an unresolvable ref; it only distinguishes resolved-null from resolved-value.)*
- **`empty`** = `True` iff the value is "empty": `null`, `""`, **whitespace-only string**, empty
  array (`[]`), or empty object (`{}`). **`nempty`** = negation.
- A unary op never reports `COERCION_FAILED` (it reads the raw value) and never reads `b`.

### 5.5 Boolean — `true false` (unary)

Read the operand coerced to **boolean** (§3). (`accepts`: `boolean`.)
- **`true`** = `True` iff effective value is boolean `true`. **`false`** = iff boolean `false`.
- Effective value `null` → both → `False`. A non-boolean that can't coerce →
  `Error(COERCION_FAILED)` (consistent with §3, since these read the coerced value, unlike §5.4).

---

## 6. Aggregation (combinator over comparator results) — strict (B4)

Let `R` = the comparators' statuses.
1. If **any** `r ∈ R` is `Error` → aggregate is **`Error`** (first error by comparator order is the
   reported one; all are available in the record).
2. Else if **any** `r ∈ R` is `Incomplete` → aggregate is **`Incomplete`** — **even if** the boolean
   is already determined (`false AND incomplete` ⇒ `Incomplete`; `true OR incomplete` ⇒
   `Incomplete`). No short-circuit masking.
3. Else (`R` is all `True`/`False`): **`and`** ⇒ `True` iff every `r` is `True`; **`or`** ⇒ `True`
   iff any `r` is `True`.

Empty `cmps` is rejected by schema validation (min 1); it never reaches aggregation.

---

## 7. Runtime routing & gating (recap from the policy)

- `True` → `true` port. `False` → `false` port.
- **`Incomplete` → `false` port** (fallback only) — and the **publish gate blocks** shipping an
  incomplete condition, so this rarely fires.
- **`Error` → fail the node**, propagating `ErrorCode`/`message`/`comparatorId`/`operand` to the
  node-execution failure surface **and** the Art. 12 audit chain (never a generic node failure).

---

## 8. Decided micro-calls (object precisely if any are wrong)

- **Epsilon:** **none (R11).** All numeric comparison (`eq`/`ne`/`gt`/`gte`/`lt`/`lte` + numeric
  membership) is **exact** IEEE-double `==`/`<`/`>`. Applying a relative epsilon to both `eq` and ordering
  broke trichotomy (a near-boundary value was both `eq` and `gt`, and both `gt` and `lte`); with one
  `number` type and typed-persisted literals, exact is the predictable choice. (Superseded the earlier
  `1e-9 * max(1, |A|, |B|)` relative epsilon.)
- **String compare:** **ordinal, case-sensitive** everywhere (eq/ordering/contains/starts/ends).
  Case-insensitivity is opt-in only via regex `(?i)`. No culture-aware collation.
- **number↔boolean:** never coerce (no `0`/`1` truthiness).
- **Ordering with a `null` operand:** `False` (predicate unsatisfied by absence, not an Error).
- **Ordering across non-null operands of differing / non-orderable types:** `Error(TYPE_MISMATCH)` →
  fail-node — never silent `false` (no defined cross-type ordering; D1/D2 policy). Edit-time-known
  mismatches are blocked by the editor (§4); this is the dynamic-ref runtime backstop. Contrast
  `eq`/`ne`, whose cross-type answer is a defined `False`/`True`.
- **Ordinal-string ordering is hinted in the editor** (§4) — lexical, not numeric.
- **Membership:** no quoting/escaping in v1; comma always splits; elements trimmed, empties dropped.
- **Regex:** backend (.NET) authoritative; FE preview best-effort on the common subset; 100 ms
  timeout + 512-char cap; invalid/timeout → `INVALID_LOGIC`.
- **Existence ops read raw; boolean ops read coerced** — the one asymmetry, intentional.

## 9. Open within this spec (flag, don't silently resolve)
- Exact number→string round-trip form parity between .NET `"R"` and JS `Number.prototype.toString`
  for edge magnitudes (exponent threshold differs). **Mitigation:** the conformance fixture only
  includes number→string cases in the safe overlap; document the boundary, revisit if a real case
  needs it.

## 10. v2 — nestable boolean tree (Phase 8)

v1 (flat: one `comb` over a comparator list) generalizes to a **tree**: a single `root`
`LogicNode` that is a **comparator leaf** (today's `{id, op, a, b?}`, identical leaf semantics §1–§5),
an **`and`/`or` group** (n-ary fold over children), or a **`not`** (unary negation of one child).
Leaf evaluation is unchanged; only aggregation recurses. The failure-routing policy, status model
(§1), per-operand error shape (B7), and task-side ref resolution (D7) all carry forward unchanged.

### 10.1 — Per-node aggregation (B9): strict dominance generalizes §6, at every level
For **every** group (both `and` and `or`) and for `not`, the child statuses fold with the **same
strict precedence as the flat model**: **`Error` dominates → then `Incomplete` → then the boolean.**
- A group is `Error` iff **any** child is `Error`; it surfaces the **first failing leaf's** B7 error in
  **depth-first, child-order** traversal (so the reported `comparatorId`/`operand` is deterministic).
- Else it is `Incomplete` iff **any** child is `Incomplete` (so `true OR incomplete = Incomplete` and
  `false AND incomplete = Incomplete` hold at **every** level — B4 generalized).
- Else it folds the booleans: `and` ⇒ all children `True`; `or` ⇒ any child `True`.
- An **empty group** (zero children) ⇒ **`Incomplete`** (no vacuous truth; the parser also forbids it,
  but the evaluator is defensive). A single-child group ≡ that child (no extra fold needed).

### 10.2 — `not` over non-boolean statuses (B8): propagate, never invert a non-verdict
`not` negates only a real boolean verdict; `Incomplete`/`Error` pass through unchanged:

| child status | `not` result |
|---|---|
| `True`       | `False` |
| `False`      | `True` |
| `Incomplete` | `Incomplete` (propagate) |
| `Error`      | `Error` (propagate the child's B7 error) |

Negating `Incomplete`/`Error` would manufacture a verdict we can't justify — inconsistent with the
failure policy. So `not` is total and never introduces a new status.

### 10.3 — Recursion bounds (B10): structural validation the flat model never needed
A tree is a new corruption / DoS surface, so the parser (BE authoritative; FE mirrors the limits)
**rejects** with `INVALID_LOGIC` any tree that violates:
- **max depth** `20` (root = depth 1; nesting deeper than this is rejected),
- **max total nodes** `200` (comparators + groups + nots, counted across the whole tree),
- **max children per group** `50` (mirrors the flat `MaxComparators`); a group needs **≥ 1** child,
- **`not` arity exactly 1** (exactly one `child`),
- **ids unique across the whole tree** (not just among sibling leaves),
- **a tree, not a DAG** — each node is its own object (JSON deserialization guarantees this); shared
  references can't occur, and the unique-id rule is the cross-check.
Leaf limits (operator catalog, literal/ref/regex lengths, kind/type) are unchanged from §1–§5.

### 10.4 — v1 ⟷ v2 equivalence + migration
`{comb, cmps}` (v1) ≡ `{ root: { kind:'group', op: comb, children: cmps } }` (v2); a **single**
comparator ≡ `root` is that bare `cmp`. The parser **accepts v1 on read and normalizes it to v2
in memory** (wrap `cmps` in a root group; lone `cmp` → bare root), so **already-published v1 `logic`
keeps evaluating unchanged**. Persisted form upgrades to v2 only on the next editor Save.
