# Condition Node Editor — Free-form Nestable Logic (Phase 8) — TODO

Evolve the Condition node from a **flat** comparator list folded by a **single** AND/OR into a
**nestable boolean tree**: AND/OR **groups** and **NOT** as first-class nodes the author composes
(e.g. `A AND (B OR NOT C)`). User-chosen direction (over "structured + templates").

**Branch:** `feat/condition-node-editor` (or a fresh `feat/condition-nesting` cut from it).
**Builds on:** the shipped flat engine (Phases 0–5) — see `condition-node-editor-TODO.md`. The
failure-routing policy, status model (B3), strict incomplete/error dominance (B4), per-operand error
shape (B7), and task-side ref resolution (D7) **all carry forward unchanged** — they just recurse.

> **Not yet wired into** `scripts/condition-handoff.mjs` (that generator parses the main TODO only).
> Same tag shapes are used here for consistency; wire in later if desired. **This prose is canonical.**

**Tag legend:** `BLOCK` = resolve before the engine work · `DECIDE` = open fork, lock before building
that part · `LOCKED` = decided · `FIX` = definite change.

---

## The one architectural insight

**The model + evaluator + backend work is identical whether the editor is auto-laid-out nested boxes
or a free-wire canvas.** Both render the *same* persisted nested tree. Only the **editor interaction**
(N1 below) differs. So the tree model, validation, evaluator, task, and migration can be built and
fully tested **before** committing to an editor-interaction style. Build the engine first; pick the
editor style at Phase 8.3.

---

## Data model — v1 (flat) → v2 (tree)

**Today (v1, flat):**
```ts
type ConditionLogic = { version: 1; comb: 'and'|'or'; cmps: Comparator[] };  // single fold
type Comparator = { id; op; a: Operand; b?: Operand };
```

**Target (v2, tree):**
```ts
type LogicNode =
  | { kind: 'cmp';   id; op; a: Operand; b?: Operand }          // leaf → boolean status
  | { kind: 'group'; id; op: 'and'|'or'; children: LogicNode[] } // n-ary fold (n ≥ 1)
  | { kind: 'not';   id; child: LogicNode };                     // unary negation
type ConditionLogic = { version: 2; root: LogicNode };           // exactly one root
```

- **v1 ≡ v2 special case:** `{comb, cmps}` is exactly `{ root: { kind:'group', op: comb, children: cmps } }`.
  A *single* comparator → `root` is that `cmp` directly (the "single condition wires straight through"
  rule, already shipped, generalizes to "a single child needs no wrapping group").
- `Operand` is **unchanged** (`lit`/`ref`, typed). Leaves are exactly today's comparators.

- **B8** — *(BLOCK)* **NOT semantics over non-boolean statuses.** Lock the truth table:
  `NOT True=False`, `NOT False=True`, **`NOT Incomplete=Incomplete`**, **`NOT Error=Error`** (propagate,
  do not invert). NOT never manufactures a verdict it can't justify (consistent with the failure policy).
- **B9** — *(BLOCK)* **Per-node strict dominance generalizes B4.** For every group (AND *and* OR) and
  NOT: **Error dominates → Incomplete dominates → then the boolean.** So `true OR incomplete = Incomplete`
  and `false AND incomplete = Incomplete` hold **at every level**. Surface the **first failing leaf's**
  B7 error (depth-first) for `Error`. Empty group children → `Incomplete`.
- **B10** — *(BLOCK)* **Recursion bounds (DoS / corruption guard) — NEW validation absent in the flat
  model.** Max tree depth, max total nodes, max children per group; NOT arity exactly 1; unique ids
  **across the whole tree**; no shared/duplicate node references (it's a tree, not a DAG). Mirror limits
  FE (`conditionModel.ts`) ⇆ BE (`ConditionLogicParser.cs`), BE authoritative.

---

## ❓ DECIDE — the editor-interaction fork (lock at Phase 8.3, NOT before)

- **N1** — *(DECIDE)* **How the author builds the tree.** Same model either way; pick the UX:
  - **(a) Nestable auto-laid-out groups (recommended first cut).** Groups render as **containers**
    (AND/OR/NOT header + nested child cards), auto-laid-out left→right; "position = meaning" preserved.
    Actions: *add comparator*, *add group*, *wrap selection in group / in NOT*, *change group op*,
    *unwrap*, *delete (re-parent children)*. No manual wiring, no cycle/orphan classes of bug. Lower
    risk, ships the nesting the user asked for.
  - **(b) Free-wire canvas.** AND/OR/NOT are **draggable nodes**; the author wires boolean outputs into
    group inputs and one node into the output anchor. Maximal power, but needs: connection validity
    (boolean-only), **single-parent (tree not DAG)**, **cycle detection**, **orphan/multi-root
    detection**, and "which node feeds output". Much larger editor; defer behind (a) unless the user
    specifically needs free positioning.
  - **Recommendation:** build the engine (8.1) and an **(a)** editor (8.3a), then add **(b)** as an
    optional later enhancement on the same model if still wanted. *(This is the user's call at 8.3.)*

---

## Phases

### Phase 8.1 — Backend tree engine (tested) — engine before editor  ✅ DONE (commits d63bc53, aeaaea4; BE 849 green)
- [x] **B8/B9/B10** locked in `docs/design/condition-operator-semantics.md` (**new §10**: tree aggregation +
      NOT table + bounds), and a **nested-aggregation conformance fixture**
      `docs/design/condition-tree.fixture.json` (25 cases, language-neutral: tree → expectedStatus), loaded
      by **both** FE and BE suites (extends the B2 mechanism via `ConditionFixtures.Tree` + csproj link).
      Leaf semantics stay in the existing fixture; leaves carry a precomputed `status`.
- [x] `ConditionLogic.cs`: `LogicNode` records (`ComparatorNode`/`GroupNode`/`NotNode`) +
      `ConditionLogic(int Version, LogicNode Root)`. **No STJ polymorphic converter needed** — the parser
      reads JsonElement manually (matching the existing flat parser), no records→JSON path at runtime.
- [x] `ConditionLogicParser.cs`: recursive validation (kinds, ops, operand kind/type, NOT arity 1,
      group ≥1 child, **B10** depth ≤20 / nodes ≤200 / children ≤50, tree-unique ids). **v1 → v2 migration
      in memory** (lone cmp → bare root; else root group). Malformed → `INVALID_LOGIC`.
- [x] `ConditionEvaluator.cs`: recursive `EvaluateTree(node) → outcome`. Per-group dominance (B9), NOT
      (B8), depth-first first-failing-leaf error surfacing (B7). Flat `Evaluate`/`Aggregate` kept (legacy
      path + aggregation fixture). All existing leaf + aggregation tests stay green.
- [x] `ConditionNodeTask.cs`: `ResolveNode` **walks the tree** resolving every leaf's `ref` (D7 recurses);
      routes `EvaluateTree`. Status→port unchanged.
- [x] `ConditionPublishGate.cs`: **no change needed** — it only calls `TryParse`, which now accepts v1/v2.
- [x] `ConditionLastRunResolver`: **no BE change** — the resolver takes a ref list as input; ref collection
      by walking the tree is a **FE** concern (Phase 8.2 `collectRefs`). Endpoint contract unchanged.
- [x] **Backward compatibility:** already-published **v1** `logic` keeps evaluating (parser migrates
      v1→v2 on read); the existing v1 task/parser tests pass unchanged. Persisted form upgrades to v2 only
      on next Save.
- [x] Tests: parser (v2 + migration + bounds + malformed), `ConditionTreeEvaluatorTests` (fixture: nested,
      NOT, dominance, depth-first error), task (nested NOT routing + deep-leaf RESOLUTION_FAILED).

### Phase 8.2 — FE pure core (tested; mirrors 8.1)  ✅ DONE (commits 1ddbdcc, e63e862; FE 248 green)
- [x] `conditionEval.ts`: recursive `evaluateTree` + `ResolvedLogicNode` (cmp/group/not) mirroring
      `ConditionEvaluator.EvaluateTree`, driven by the **same** `condition-tree.fixture.json` (26 FE tests
      green) → FE/BE conformance for nesting. (commit 1ddbdcc)
- [x] **`conditionTree.ts`** (new, cohesive module so the flat editor stays green): draft tree
      (`DraftNode` = cmp/group/not; a leaf IS a `DraftComparator`) + recursive `coerceTreeToLogic` →
      **v2** (tree-unique ids; B10 depth/nodes/children) + `logicToTree` (hydrates **v1 flat AND v2 tree**)
      + edits `addComparator`/`addGroup`/`wrapInGroup`/`wrapInNot`/`setGroupOp`/`removeNode` (cascades
      emptied NOTs)/`unwrap` (splices group into parent). **No `moveNode`** (N1-a). Reuses the flat
      leaf helpers (`coerceOperand`/`persistedToDraft`/`newComparator`, now exported). 22 tests.
- [x] `conditionSummary.ts`: recursive `summarizeTree` with precedence parens, e.g.
      `1 = 1 AND (2 = 2 OR NOT 3 = 3)`. 3 tests.

### Phase 8.3 — Editor (lock N1 here)
- [x] **N1 LOCKED = (a) auto-laid-out nested boxes** (user-chosen). AND/OR/NOT render as containers with
      nested child cards, auto-laid-out; actions = add comparator/group, wrap selection in group/NOT,
      change op, unwrap, delete. **No free-wire** → no `moveNode`, no cycle/orphan/connection-validity
      machinery. (b) free-wire stays an optional later enhancement on the same model if ever wanted.
- [x] **Editor built as recursive HTML container boxes (not @xyflow/react).** For N1-a the nesting reads
      best as nested `<div>` boxes, which also makes it jsdom-testable without a React Flow mock. So
      `conditionLayout.ts`/`conditionFlow.ts` are **not used** for the tree editor (the flat ones are now
      dead — cleanup follow-up). New: `ConditionTreeNodes.tsx` (`TreeNodeBox`→`GroupBox`/`NotBox`/`CmpBox`,
      leaf reuses `OperatorMenu`+`InputEditor`), `conditionTreeContext.ts`, `conditionTreeCss.ts`.
- [x] Editor actions wired (N1-a): add comparator/group, wrap in group/NOT, change op, unwrap, delete;
      "Add condition" promotes a bare/NOT root into a group so adds always land. (No connect/disconnect — (b) deferred.)
- [x] `ConditionTreeEditorView.tsx`: always-on live re-eval across the tree (`evaluateDraftTree`), output
      pill, value-source switcher, **Save gated on `coerceTreeToLogic`**; opens BOTH v1 & v2, saves v2.
- [x] `ConditionLogicField.tsx` wired: mounts the tree editor, summary renders v1 rows OR v2 expression
      (`summarizeTree`), last-run refs walk the tree (`collectTreeRefs`), Save writes v2 + strips legacy.
- [x] Component tests (jsdom, no mock): live OR→AND flip, NOT negation, delete empties + re-blocks Save,
      save-as-v2, dirty-confirm; field v1/v2 summary + v2-save + cancel-guard. FE 266 green.

### Phase 8.4 — Round-trip + portability + E2E  ⏳ (portability done; E2E pending)
- [ ] Save → publish → run routes a nested condition end-to-end (preview verification — **needs the app
      running**; also closes the long-standing R10 browser E2E).
- [x] Template/bundle round-trip of v2 `logic` **proven**: `CredentialSlotModuleTests` round-trips a v2
      tree (group → cmp + not(cmp), ref operand in a deep leaf) byte-identical through extract→rebind while
      a sibling credential still slots — the shared walk treats the deeper tree the same as v1 (refs ≠ slots).
- [x] **Dead-code cleanup DONE** (commit 33a23a1): removed the flat `ConditionEditorView`/`conditionFlow`/
      `conditionLayout`/`ConditionNodes`/`conditionNodeTypes`/`conditionEditorContext` (+ their tests) — the
      app uses only the tree editor now. *(Minor orphans left, low value: flat `coerceDraftToLogic`/
      `logicToDraft`/`toResolvedCondition`/`evaluateDraft`/`removeComparator`/`newCondition` are now only
      test-referenced but live in shared modules whose leaf helpers the tree path still uses — prune later if desired.)*
- [ ] Migrate `condition-node-editor-TODO.md` (mark flat model superseded by v2) + final memory note;
      full FE + BE suites green.

---

## Risk register (the genuinely hard parts)
1. **Polymorphic JSON** on records in System.Text.Json (BE) and discriminated unions (FE) — get the
   `kind` converter right both ways; round-trip tests first.
2. **Editor UX for nesting** (N1) — the largest, most uncertain piece; (a) de-risks it vs (b).
3. **Strict dominance at every level** (B9) + **NOT over non-boolean** (B8) — easy to get subtly wrong;
   the shared nested fixture is the guardrail.
4. **Recursion bounds** (B10) — new attack surface the flat model never had.
5. **v1 backward-compat** — published workflows must not break on read.

## Process reminder (user's standing instruction)
Implement **step by step**, **unit tests for every feature**, pure-helper + integration split.
**Ask rather than assume on the N1 editor fork** and any new DECIDE items.
