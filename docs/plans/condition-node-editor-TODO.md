# Condition Node Editor — TODO

Redesign the **Condition** node's configuration from the side-panel `LEFT / OPERATOR / RIGHT`
form into a **live, in-canvas dataflow graph**: reference/literal **inputs** → **comparators**
(operator per comparator) → one **AND/OR combinator** → the **TRUE/FALSE output**, evaluated live.

Design handoff: `~/Downloads/Knot Garden Design/design_handoff_condition_node_editor/`
(`README.md` = visual spec, `IMPLEMENTATION.md` = engineering recipe). The `.jsx`/`.html` are
throwaway prototype scaffolding — port the *visuals, evaluation, inline editors, styling*, not the
React structure, and **not** the prototype's loose JS evaluation semantics (see Phase 0).

**Branch:** `feat/condition-node-editor`
**Frontend dir:** `Frontend/` · vitest `npx vitest run` · types `npx tsc -b` · lint `npx eslint <files>`
**Backend:** `dotnet test` (KnotGarden.Tests / KnotGarden.NodeRuntime.Tests)

> **Derived artifact:** `condition-node-editor.handoff.json` is generated from this file by
> `node scripts/condition-handoff.mjs`. **This prose is canonical**; the JSON is regenerated, never
> hand-edited. `node scripts/condition-handoff.mjs --check` fails on drift (run it in CI). Tagged
> ids below use a strict one-line shape so the generator can parse them:
> `- **<ID>** — *(<TAG>, <Phase>)* <text>` where TAG ∈ {BLOCK, DECIDE, LOCKED}.

**Tag legend:** `BLOCK` = resolve before Phase 1 (shapes the engine) · `DECIDE` = open fork, lock
before building that part (**do not assume**) · `LOCKED` = decided · `FIX` = definite change, apply
when its phase is reached.

---

## Decisions locked (with the user)

1. **Surface:** a **full-screen editor view** (like `NodeEditorShell`), opened from a selected
   Condition node; the properties panel shows a compact read-only summary + an "Edit logic" button.
2. **Backend:** **full engine now** — multi-comparator AND/OR, all operators (incl. unary),
   reference/literal operands, evaluated server-side at run time.
3. **Operand references:** **reuse the existing variable system** (`variable_ref` / `{{ … }}`).
4. **Live values:** **selectable source** — *Last run* / *Dry run* / *Manual sample* (always-on
   fallback). **Manual sample + last-run is the first usable release; dry-run is deferred (Phase 6).**
5. **Evaluation semantics:** **type-aware** (NOT JS-loose), spec in Phase 0, identical FE/BE,
   enforced by a shared conformance fixture.

## 🔒 Failure-routing policy (the single rule the forks collapse into)

**Nothing that represents failure or ambiguity ever silently routes to the `false` port.**
- `Incomplete` is the **only** state that falls back to `false` at runtime — and the **publish gate
  blocks it from shipping**, so the fallback rarely fires.
- Everything genuinely broken is **`Error` → fail-node**, carrying an `ErrorCode`.
- **Expected absence** (optional enrichment node didn't run, field legitimately missing) is **not an
  error**: it resolves to a legitimate `null`, which the author models with `exists`/`empty`. Graceful
  degradation is preserved without being the silent default.

**Result status (B3):** `True | False | Incomplete | Error`. Resolved JSON `null` is a legitimate
value for unary existence ops — distinct from `Incomplete` and `Error`.

**ErrorCode taxonomy (all → fail-node):**
- `INVALID_LOGIC` — schema violation, unknown/foreign operator id, malformed structure. Should never
  reach runtime if the publish gate + schema validation hold; if it does, it's corruption → fail loud.
- `RESOLUTION_FAILED` — a ref valid at design time is unresolvable at run. Routing `false` because
  data couldn't be resolved is exactly the audit hazard to avoid → fail-node.
- `COERCION_FAILED` — a successfully-resolved value can't be brought to its **own declared type**
  (declared `number`, runtime `"abc"`). **Not** cross-type comparison: `number eq string ⇒ false`
  has defined semantics and is a legitimate `False`, never `COERCION_FAILED`.
- `TYPE_MISMATCH` — an **ordering** op (`gt/gte/lt/lte`) over two resolved, non-null operands whose
  effective types **differ** (or are the same but **non-orderable**, e.g. both `boolean`). Ordering
  has no defined cross-type answer, so routing to `false` would be a silent wrong branch → fail-node.
  Reachable only via dynamic refs; the editor blocks edit-time-known mismatches (this is the runtime
  backstop). **Distinct from `COERCION_FAILED`** (one value vs its *own* declared type) and from
  cross-type **`eq`/`ne`**, which stay a defined `False`/`True`. (`operand: null` — pair-level.)
- `INTERNAL_INVARIANT` — a structurally-valid condition reached an **impossible runtime state** (e.g.
  evaluated to `Incomplete`, which the resolution model + publish gate make unreachable). Root cause is
  **our bug or a bypassed gate**, NOT malformed inbound data — kept distinct from `INVALID_LOGIC` so an
  audit query for "schema corruption" isn't polluted by "our evaluator hit an impossible state" (R1/R6).

**Per-operand error shape (B7, defined in Phase 0; ONE shape for both paths):** the runtime
evaluator and the Phase 6 dry-run emit the **same** object so editor-preview failures and runtime
failures are one diagnostic type:
`{ code: ErrorCode, message: string, comparatorId: string, operand: 'a'|'b'|null }`.

---

## ⛔ BLOCK — settle before writing the evaluator

- **B1** — *(BLOCK, Phase 0)* Operator-semantics spec (kills "mirror prototype"): invariant-culture
  number parsing; ordinal vs ordinal-ignore-case compare; concrete epsilon value + formula applied
  consistently across `eq` and ordering ops; `contains`/`starts`/`ends` case sensitivity; regex
  flavor + options + timeout + invalid-regex behavior; membership parsing (trim, empty elements,
  escaped/quoted commas, per-element typed conversion); `empty` over null/`""`/whitespace/empty
  array/object; `exists` (non-null vs resolved vs property-present-when-null); boolean operators on
  non-boolean operands; `eq` on arrays/objects (unsupported vs structural).
- **B2** — *(BLOCK, Phase 0)* Shared conformance fixture: language-neutral JSON
  `(op, typeA, A, typeB, B) → expectedStatus` consumed by both FE and BE suites, plus a catalog
  fixture (`id, arity, accepts`) for drift detection. The enforcement mechanism for "what you see ==
  what runs".
- **B3** — *(BLOCK, Phase 0)* Result is a status (`True|False|Incomplete|Error`) + record, not
  `bool?`, identical FE/BE.
- **B4** — *(BLOCK, Phase 0)* Incomplete aggregation is strict: any incomplete comparator ⇒ whole
  condition `Incomplete`, even if another comparator already determines the boolean. Test
  `false AND incomplete` and `true OR incomplete`.
- **B5** — *(BLOCK, Phase 1)* Operand `type` is non-optional at persistence — it must survive
  `EvaluatePropertyValue` (which replaces a `ref` with the raw value), or BE can't reproduce
  cross-type-equals-false.
- **B6** — *(BLOCK, Phase 1)* Exhaustive legacy operator-name → OperatorId map, enumerated from the
  legacy `ConditionOperator` enum, with a contract test (next to the catalog fixture) asserting no
  legacy operator is unmapped.
- **B7** — *(BLOCK, Phase 0)* Per-operand error shape (above) defined once; runtime + dry-run emit it.

## ✅ LOCKED forks

- **D1** — *(LOCKED, Phase 1)* Error → **fail-node** with a code (`INVALID_LOGIC` /
  `RESOLUTION_FAILED`). Never route `false` on failure.
- **D2** — *(LOCKED, Phase 1)* Uncoercible resolved value → **`Error` (`COERCION_FAILED`)**, not
  `Incomplete` (the comparator is fully configured). Cross-type-with-defined-semantics stays a
  legitimate `False`.
- **D3** — *(LOCKED, Phase 1)* Operands **colocated on the comparator** (`{ id, op, a, b? }`); UI
  cards derived from the model.
- **D4** — *(LOCKED, Phase 1)* Exhaustive legacy map (B6); residual unknown operator = corrupted/
  foreign data → `INVALID_LOGIC` (block on open/publish; fail-node at runtime for an
  already-published workflow). **Never silently substitute a default operator.**
- **D7** — *(LOCKED, Phase 1)* Task-side ref resolution. `logic` is `Expression:false` (handed over
  unresolved); the task resolves each `ref` via a new `IWorkflowState.TryResolveVariable` that reports
  found-ness, so missing ref → `RESOLUTION_FAILED` and resolved-`null` stays a legitimate value. The
  generic executor resolution collapses missing→`null`, which would destroy that distinction. Requires
  honoring `Expression:false` in the executor loop (primitives still unbox); this also makes the
  literal-`{{`-escape rule moot.

## ❓ DECIDE — still open (lock at the relevant phase)

- **D5** — *(LOCKED, Phase 5)* Last-run scoping & exposure. **Decided:**
  - *Scope:* the **most recent `ExecutionInstance` for the workflow definition** (any version), by
    `CreatedAt`. Response carries `runId` / `versionId` / `createdAt`.
  - *Staleness:* `stale = run.WorkflowVersionId != ActiveWorkflowVersion` (when both known); the editor
    shows the run timestamp + a **"stale"** badge so a value from an older version is visibly flagged.
  - *Resolution:* backend-authoritative — reuses the runtime `ExpressionEvaluator` over a read-only
    projection of the stored run (found-ness preserved), so editor-shown == runtime-resolved.
  - *Exposure:* last-run values are a **strict subset of what `GET /api/executions/{id}` already returns**
    to the same authenticated user (the Execution UI shows full node outputs today), so no new trust
    boundary. `SecretValue`-wrapped credentials are never persisted to a run. **Defense-in-depth:** any
    value whose ref names a sensitive field (`secret|token|password|apikey|authorization`) is returned
    masked as `"***"` (`sensitive: true`). *(Override this if compliance needs last-run values gated
    behind a separate permission or fully withheld.)*
- **D6** — *(LOCKED, Phase 7)* Templated legacy condition migration: **no proactive auto-migration.**
  Decided after R9 surfaced that legacy `left/operator/right` carries **no operand types**, while persisted
  `logic` requires a declared type per operand (load-bearing: cross-type `eq`→False, ordering coerces to
  the declared type). Any publish/import/first-run migration would have to **guess** types with no resolved
  values to infer from — defaulting a ref to `string` silently flips the common case (`count gt 5` with
  `count=10`: legacy infers number → True; auto-migrated string → ordinal `"10">"5"` → False). The only
  safe auto-migratable subset is literal-vs-literal (types unambiguous), which is near-empty in practice.
  So: **legacy conditions keep running via the legacy path** (the runtime infers types from *resolved*
  values, semantics intact) and migrate to `logic` **only via the editor's one-way Save**, where the author
  reviews/sets operand types. (Considered & rejected: migrate-on-publish/import/first-run — all lossy for
  ref operands; migrate-only-literals — safe but migrates ~nothing.)

---

## Data model

**Persisted runtime model** (`properties.logic`) — typed, validated:

```ts
type Operand =
  | { kind: 'lit'; type: 'string'|'number'|'boolean'; value: string|number|boolean }  // typed, not stringly
  | { kind: 'ref'; type: Type; ref: VariableRef | string };   // type non-optional (B5)

type Comparator = { id: string; op: OperatorId; a: Operand; b?: Operand };  // colocated (D3)
type ConditionLogic = { version: 1; comb: 'and'|'or'; cmps: Comparator[] };  // cmps min 1
```

- `FIX` **Literals persist typed** (not strings) → no runtime parsing of `"NaN"`, whitespace, locale
  decimals, exponent notation, boolean casing.
- `FIX` **Separate editor draft model** (textual/incomplete) from the persisted runtime model (typed
  JSON); coerce draft→typed on Save.
- `FIX` **Persisted-schema validation** (reject malformed, don't silently accept): `version==1`;
  comparator-count limit; unique comparator ids; known operator ids; valid `kind`/`type`; `b`
  absent/ignored for unary; max literal / ref / **regex** lengths.
- ~~`FIX` **Literal-string `{{` escape/skip rule**~~ — **moot under D7**: literals are never
  expression-resolved (the task reads `lit` values raw; only `ref` operands are resolved, task-side).

## Operator catalog (shared FE/BE source of truth)

From `operator-dialog-data.jsx → OPERATORS`, `{ id, group, label, symbol, arity, accepts[] }`.
Comparison `eq ne gt gte lt lte` · Text `contains ncontains starts ends regex` · Membership
`in nin` · Existence (unary) `empty nempty exists nexists` · Boolean (unary) `true false`. Validated
against the **catalog fixture (B2)** in both languages.

---

## Phases (editor before the dangerous subsystem)

### Phase 0 — Semantic contract + fixtures  ✅
- [x] **B1** Operator-semantics spec → `docs/design/condition-operator-semantics.md`.
- [x] **B2** Shared conformance fixture + catalog fixture JSON, loadable by both FE and BE suites.
      → `docs/design/condition-conformance.fixture.json` + `docs/design/condition-catalog.fixture.json`.
- [x] **B3/B4** Status model + strict incomplete-aggregation specified (spec §1, §6).
- [x] **B7** Per-operand error shape defined (one shape, both paths) (spec §1).

### Phase 1 — Backend model / evaluator / task / legacy (tested)  ✅
- [x] `ConditionLogic` records + typed JSON (de)serialization; schema validation (FIX list).
      → `ConditionLogic` + `ConditionLogicParser` (+ `ConditionLogicParserTests`).
- [x] Server operator catalog verified against the **catalog fixture (B2)**.
      → `ConditionOperatorCatalog` + `ConditionOperatorCatalogTests` (id/group/arity/accepts/order drift).
- [x] Pure `ConditionEvaluator` → **status model (B3)**; strict aggregation (B4); fixture-driven (B2).
      Apply **D1/D2** error codes + `TYPE_MISMATCH` for cross-type / non-orderable ordering (§5.1).
      → `ConditionEvaluator` + `ConditionEvaluatorTests` (all conformance + aggregation cases green).
- [x] Rewrite `ConditionNodeTask`: resolved `logic` → evaluate → `selectedPort`. `Incomplete →
      false`; `Error →` **fail-node**. `FIX` **task contract: propagate `ErrorCode`/`Message` +
      failing comparator/operand id**. → done; the code+comparator+operand are encoded into the
      `Failure` message (which flows to the journal/audit chain). **CAVEAT:** `LegacyNodeResult.Failure`
      has no structured `ErrorCode` field, so it's encoded in the string — a dedicated field would be a
      cross-cutting failure-surface change (deferred follow-up).
- [x] Legacy: **B6** map; `FIX` precedence **valid `logic` > legacy `left/operator/right` >
      configuration error**; **D4** residue → `INVALID_LOGIC`; not-equals → **`ne`** (verified vs the
      shipped `NotEqual` enum name). → `LegacyConditionMap` (+ `LegacyConditionMapTests` B6 drift guard).
      Legacy nodes without `logic` run via the legacy path directly (stay legacy on disk).
- [x] Manifest: `logic` param (`"json"`, `Expression:false`) added; `true`/`false` outputs kept.
      Hiding from the generic `ManifestForm` is a **frontend** concern (Phase 4).
- [x] **D7 resolution model (LOCKED): task-side resolution.** The generic executor
      resolution collapses *missing ref* into `null` (`WorkflowStateProjection.GetVariable` returns
      `default`), which would destroy the `RESOLUTION_FAILED` vs resolved-`null` distinction (§2.3 vs
      §5.4) — the centerpiece of the failure-routing policy. So the **task resolves its own ref
      operands**: `logic` is `Expression:false` (handed over unresolved), and the task resolves each
      `ref` via an exposed `IWorkflowState.TryResolveVariable(name, out value)` (new) that reports
      found-ness → **missing ⇒ `RESOLUTION_FAILED`**, resolved-`null` ⇒ legitimate `null`.
      → `IWorkflowState.TryResolveVariable` (default + precise overrides in both state projections);
      task resolves refs in `ConditionNodeTask.ResolveOperand`.
- [x] `FIX` **Honor `Expression:false` in the executor** (enables D7): resolution loop now gates on
      the manifest `Expression` flag (skips `variable_ref`/`{{ }}` resolution, still unboxes
      primitives) at both the main + parallelForEach body call sites. Full backend suite green (941).
      Renders the literal-`{{`-escape FIX moot.
- [x] `FIX` **Task-side resolution contract tests**: `variable_ref` in `ref`; resolved `null`
      (legitimate, via exists/nexists) vs **missing reference (`RESOLUTION_FAILED`)**; coercion/type
      errors → fail-node. → `ConditionNodeTaskLogicTests`.
- [x] Tests: `ConditionEvaluatorTests` (fixture-driven) + `ConditionNodeTaskTests` (legacy) +
      `ConditionNodeTaskLogicTests` (logic path).

### Phase 2 — FE pure core (tested; no value-API dependency)  ✅
- [x] `operators.ts` (catalog-fixture verified) + `conditionEval.ts` → **status model**, driven by
      the **same conformance fixture (B2)** → early FE/BE conformance.
      → `Frontend/src/node-editor/condition/{operators,conditionEval}.ts` (+ `.test.ts`); both suites
      load the shared `docs/design/condition-*.fixture.json` via `src/test/repoFixture.ts` (Node
      surface typed by `src/test/node-shims.d.ts` — deliberately NOT full `@types/node`, which would
      retype DOM globals and break unrelated tests). 106 tests green; `tsc -b` + eslint clean. Regex is
      backend-authoritative (FE translates only a leading inline-flag group, no ReDoS timeout).
- [x] `conditionModel.ts`: legacy⇄logic conversion, add/remove re-flow, unary B-drop, defaults,
      draft⇄persisted coercion. Pure + tested. → draft/persisted/legacy shapes + `newCondition`,
      `addComparator`/`removeComparator` (deterministic `c<n>` ids, no renumber), `setOperator`
      (unary B-drop / binary B-seed of A's type), `setOperandType`/`setOperandKind`,
      `coerceDraftToLogic` (→ logic only when fully valid; else `DraftIssue[]` unset/invalid/structure;
      parses literals via the evaluator's exported `parseInvariantNumber`/`parseBool` so editor-accepts
      == runtime-accepts), `logicToDraft`, `legacyToDraft` (**best-effort seed** — `{{ }}`→ref, else
      type-inferred lit; unmappable op → null draft). Limits mirror ConditionLogicParser.cs.
- [x] `conditionLayout.ts`: pure column→coordinate geometry (4 cols inputs→comparators→combinator→
      output), feeding @xyflow/react in Phase 3. Deterministic; comparator centered between its
      operands, combinator/output at the comparators' vertical center. Pure + tested.

### Phase 3 — Editor view on **Manual sample only**  ⏳ (slice 1 done; slice 2 next)

**Locked forks (with the user):** manual sample values edited **inline** on each input; build in
**incremental slices**; **block Save until the draft fully coerces** (no 'unset' encoding in persisted
logic). Graph on `@xyflow/react`; reference picker reads the typed variable store.

**Slice 1 — DONE (fork-free core + the static live graph; 30 tests):**
- [x] Pure foundations: `conditionPreview.ts` (draft→`ResolvedCondition` via a pluggable value
      provider; **preview never emits RESOLUTION_FAILED** — a ref with no sample is Incomplete) +
      `operatorFilter.ts` (type-aware filtering — `any` means "applies when type unknown", not a
      wildcard; the **edit-time cross-type ordering block** + **ordinal-string hint** FIXes) +
      `conditionFlow.ts` (pure node/edge builder off `conditionLayout` + the evaluator outcome).
- [x] `ConditionEditorView.tsx` on `@xyflow/react` + custom nodes (`ConditionNodes.tsx`: input/
      comparator/combinator/output) + scoped `.cne` CSS (`conditionEditorCss.ts`, README tokens, inline
      `<style>` per the NodeEditorShell precedent). Live re-eval, wires, **output pill**, AND/OR toggle,
      add/remove comparator, incomplete/error display, **Save gated on a valid draft** + Back/Cancel.
- [x] Component tests (mock `@xyflow/react`): Save gating, live AND↔OR re-eval, add, Back. + pure tests.

**Slice 2a — DONE (inline editing layer + lifecycle; +13 tests, 172 total green):**
- [x] Inline editors: `OperatorMenu.tsx` (type-aware filter via `operatorsForType`, grouped + searchable,
      unary marker, current-op check, **edit-time cross-type ordering ops shown disabled** +
      **ordinal-string hint**) + `InputEditor.tsx` (Reference picker over `RefOption[]`; Literal
      string/number/boolean segmented; **inline manual-sample** field for the chosen ref). Both unit-tested.
- [x] Wired into the nodes via `ConditionEditorContext`: op-pill click opens the menu (unary B-drop on
      pick via `setOperator`), input click opens the editor; operand + sample edits flow back to the
      view's draft/sample state and re-evaluate live. `conditionFlow` now carries the operand + operand
      types on node data.
- [x] `FIX` **Editor lifecycle (partial):** draft local until Save; **dirty-state confirm on Back/leave**
      (tested). Save still gated on a fully-valid draft.

**Slice 2b — DONE (entry + persistence; +4 tests, 176 total green):**
- [x] Full-screen entry: `ConditionLogicField.tsx` replaces the Condition node's generic `ManifestForm`
      fields (gated on `manifest.id === 'condition'`) with a one-line status + **"Edit logic"** button
      that mounts `ConditionEditorView` as a `position: fixed` overlay inside the selected node's panel.
      Variables map from `useVariableStore` → `RefOption[]` (via `variableRefExpression`); resolved
      variable values seed the live-preview manual samples.
- [x] On Save: writes the typed `logic` object onto the node and **removes `left/operator/right`** (the
      one-way migration). Node-deleted-while-open guard is inherent — the panel unmounts the overlay.
      `logic` persists as the object (backend `ConditionLogicParser.ToElement` accepts object|string).
- [x] Integration tests (`ConditionLogicField.test.tsx`): summary states (unconfigured/legacy/configured),
      open/close, legacy-seed, and **Save writes logic + strips legacy** (other props preserved).
- [ ] `FOLLOW-UP` Interactive browser E2E (node-click → popover → live re-eval) — needs the backend
      manifests stood up; deferred (covered at the unit/integration level for now).

### Phase 4 — Properties-panel integration  ✅ (DONE — +14 tests; FE 180 condition/entry green, BE 796 green)
- [x] Compact **read-only summary** in the panel: `conditionSummary.ts` (pure `summarizeLogic` →
      operand·operator·operand rows + combinator; short ref paths, quoted strings, unary B-drop, unknown-op
      fallback) rendered by `ConditionLogicField` (header + "Edit logic" button; legacy/unconfigured fall
      back to a one-liner). (The slice-2b minimal launcher is now the full summary.)
- [x] `FIX` **Guard:** normal save passes `properties` verbatim (`schemaMapper.toBackend` spreads them) —
      only the editor's `onSave` strips `left/operator/right`. Tested: **Cancel does not call `onChange`**
      (legacy preserved); only Save migrates.
- [x] `FIX` **Publish-time gate:** `ConditionPublishGate.FindIncompleteConditions` (Features) — a condition
      is publishable with a **valid `logic`** (must parse) OR a **usable legacy `operator`**; neither →
      blocked. Wired into BOTH `/publish` and `/activate/{versionId}` in `Program.cs`, mirroring the
      unbound-slot gate (400 + `{ message, incompleteConditions }`). Drafts (`/versions`) intentionally
      ungated.
- [x] Tests: `ConditionPublishGateTests` (valid/legacy/none/malformed/empty/non-condition/ordering) +
      `conditionSummary.test.ts` + the `ConditionLogicField` summary + cancel-guard cases.

### Phase 5 — Last-run value resolution API  ✅ (DONE — +10 tests; FE 184 condition/entry green, BE 802 green)
- [x] Endpoint `POST /api/workflows/{id}/condition-values` (refs → resolved values + run provenance),
      backed by `ConditionLastRunResolver` (Features) — reuses the runtime `ExpressionEvaluator` over a
      read-only `IWorkflowState` projection of the last run; found-ness + sensitive-masking. Resolves **D5**.
- [x] Editor value-source switcher (**Last run** ⇆ **Manual sample**): `ConditionEditorView` takes a
      `lastRun` prop (provider swaps source live); `ConditionLogicField` fetches on open (refs from the
      logic graph), shows run timestamp + **stale** badge. Manual stays the always-on fallback.
- [x] Tests: `ConditionLastRunResolverTests` (node-output / variable / nested / miss / resolved-null /
      sensitive-mask), view switcher (last-run resolves → TRUE, manual → incomplete, stale badge),
      `ConditionLogicField` fetch-on-open passes values to the editor.
- [ ] `FOLLOW-UP` (carried from Ph3): interactive browser E2E of the editor — needs backend manifests up.

### Phase 6 — Dry-run capability framework (deferred; build only if justified)  ⏳
- [ ] `FIX` **PreviewCapability per node:** `None | CacheOnly | ReadOnly | Sandboxed`; preview
      executor runs only safe-preview nodes.
- [ ] `FIX` **Scope = minimal transitive dependency closure of referenced operands, deduped**, bounded
      by timeout.
- [ ] Editor **Dry run** source; per-operand failures (B7 shape) shown, non-blocking.
- [ ] Tests (closure scoping, capability gating, partial failure, timeout).

### Phase 7 — Round-trip + E2E  ⏳
- [ ] Save → publish → run routes TRUE/FALSE end-to-end (preview verification).
- [ ] Template/bundle portability of `logic`; resolve **D6**; `slot:`/param interplay if relevant.
- [ ] Memory note; update this TODO; full FE + BE suites green.

---

## Review feedback follow-ups (external review — the "absence boundary" is the crux)

The review's structural finding holds: nearly every real seam lives on the treatment of **absence**
(Incomplete vs null vs RESOLUTION_FAILED). Dispositions:

- **R1** — *(DONE)* **Runtime `Incomplete` → fail-node, not `false`.** Closed the one probabilistic hole
  in the structural failure policy. Confirmed runtime evaluator-`Incomplete` is **structurally
  unreachable** (Lit → value; Ref → value or Unresolved→RESOLUTION_FAILED; legacy always builds value
  operands; parser requires ≥1 cmp), so this is a zero-behaviour-change backstop that fails loud if the
  publish gate is ever bypassed/stale. `ConditionNodeTask.Route` returns `Fail(`**`INTERNAL_INVARIANT`**`)`
  for `Incomplete` — a NEW code distinct from `INVALID_LOGIC` (our-bug vs bad-inbound-data; keeps audit
  queries clean — reviewer's refinement). Added to the BE enum, FE union (parity), and the taxonomy above.
  (The *entirely-unconfigured* node still routes `false` — see **R7**, resolved.)
- **R2** — *(DONE + verified end-to-end)* **Editor preview miss is source-aware** (`conditionPreview.ts`):
  manual miss = Incomplete; **authoritative miss (last run / dry run) = RESOLUTION_FAILED**
  (`PreviewResolution.authoritativeMiss`). **Wire-format checked** (the way this can pass unit tests yet
  be wrong end-to-end): the endpoint serializes each ref as `{found, value, sensitive}` with **`found` as
  its own field** (`Program.cs`), the resolver includes **every** requested ref incl. misses (no key-drop),
  legit-null arrives as `{found:true, value:null}`, and the FE provider keys off `hit?.found` (not
  presence/value). The miss/null collapse cannot occur — found-ness survives transport.
- **R4** — *(DONE — audited, no bug; outranks R3)* **`Expression:false` honored at every resolution site.**
  There are exactly two resolution entry points — main (`WorkflowExecutor.cs` ~690) and parallelForEach
  body (~1276) — and **both gate on the per-param manifest flag** (`NonExpressionParams` /
  `bodyNonExpressionParams`); the `EvaluatePropertyValue` recursion threads the flag through. **Retry**
  re-enqueues a work item that re-runs the node through the main path (Retry.cs doesn't resolve inputs);
  **resume** reuses snapshots already stored unresolved via the gated path. So the missing-vs-null
  distinction survives resume/retry/sub-workflow. (Reviewer correctly prioritised this above R3 — a live
  bug would silently break every resumed workflow — but the code is sound.)
- **R3** — *(DONE)* **D7 resolution-parity test.** B2 proves *evaluator* parity given identical resolved
  inputs; nothing proved the *resolution layer*. `ConditionResolutionParityTests` now pins it: on the
  **same** `WorkflowStateProjection` (exposed `internal` via `InternalsVisibleTo`), a **found** ref —
  direct global, promoted node-output, and present-but-`null` — resolves bit-identical through
  `TryResolveVariable` and the generic `GetVariable<object>` path, so a condition can't see a different
  value than the node feeding it. The lone deliberate divergence (a genuine **miss** → not-found, which
  the generic path can only collapse to `null`) is asserted as the extra found-ness bit D7 needs.
- **R5** — *(DONE — dropped)* **Name-substring masking removed** (`ConditionLastRunResolver`). Decision
  hinged on whether the variable schema carries a secret/sensitivity type — it does **not**
  (`VariableRecord.type` is only `string|number|boolean|object`), so per the reviewer's rule we take (a)
  and drop it: with secrets never in a run it only produced **false positives** (masking
  `password_policy_enabled`) and missed real names (`bearer`/`pat`). The trust-boundary argument ("strict
  subset of `/api/executions/{id}`") stands alone. `LastRunRefValue.Sensitive` kept (always false,
  vestigial) for response-contract stability; re-enable via a structural typed-as-secret signal if added.
- **R6** — *(DONE)* **Structured `ErrorCode` field in the failure surface.** `LegacyNodeResult.Failure`
  gained an optional `ErrorCode` (backward-compatible; all existing `new Failure(msg)` sites unchanged).
  `ConditionNodeTask.Fail` now sets it (`error.Code.ToString()`) while keeping the `[CODE]`-prefixed human
  message. The executor's single node-failure chokepoint threads it into `CreateFailureJournalData`, which
  writes a **discrete `errorCode` key** into the (hash-chained) journal entry `Data` — so the Art-12 audit
  field-filters on the code instead of substring-matching the message. **No DB migration:** `ExecutionJournal.Data`
  is schemaless JSON, so the new key just enters the hash chain for new entries. Tests: `ConditionNodeTaskLogicTests`
  (typed field populated incl. `TYPE_MISMATCH`) + `ExecutionEngineTests` (journal carries `errorCode`;
  a codeless failure omits the key). Full BE suite 809 green. **Scope note:** the `parallelForEach`
  body collapses a per-item failure to a message string, so the discrete field is recorded on the
  top-level single-node path only; the per-item `[CODE]` prefix keeps substring access there. NodeState
  has no `ErrorCode` column (operational display stays the message) — a column would need a migration, out
  of scope for the audit-query goal.
- **R7** — *(DONE — resolved as-is, not a DECIDE)* **Unconfigured-at-runtime stays `false`.** An entirely
  unconfigured node (no logic, no legacy) is **expected-during-authoring absence**, not failure or
  ambiguity — the failure-routing policy doesn't cover it. Routing `false` for a never-saved node on a
  manual draft run is consistent with the rule as written; forcing fail-node here would just be hostile
  authoring UX. Kept as-is (deliberate, tested: `Empty_logic_string_falls_through_to_unconfigured_false`).
- **R8** — *(DONE)* **FE regex preview freeze closed by a synchronous ReDoS-shape guard.** `conditionEval.ts`
  now refuses to execute a pattern with the classic catastrophic-backtracking shape — an unbounded
  quantifier over a subexpression that itself contains one (`(a+)+`, `(a*)*`, `(.*)*`, `(\d+)+`,
  `(a{2,})+`) — surfacing a clear operand-pinned `INVALID_LOGIC` ("evaluated on the server") instead of
  running `re.test` on the UI thread. Conservative single-flat-group detector (false positive only
  defers a benign pattern to the server, which stays authoritative under its own ReDoS cap); the detector
  is itself linear so it's safe up to `MAX_REGEX_LENGTH`. Chose the synchronous guard over a worker/
  deadline to avoid async-infecting the conformance-shared evaluator. Tests prove safe patterns still
  evaluate and the canonical `(a+)+$`-vs-`"aa…!"` input returns instantly instead of hanging.
- **R9** — *(portability DONE; D6 still open)* **Template/bundle portability of `logic`** pulled out of
  Phase 7 and **proven**: `CredentialSlotModuleTests` now show a condition node's nested `logic` blob (a
  ref operand + a literal) round-trips through `ExtractIdsToSlots`→`RebindSlotsToIds` **byte-identical**
  (operands are variable refs, not credential slots, so the shared recursive walk leaves them untouched
  while a sibling credential still slots/rebinds), and that `SubstituteParameters` **descends into**
  `logic` (a `{{param:…}}` inside a logic literal is substituted) — it neither chokes nor special-cases
  the tree. **D6 (legacy-templated-condition migration timing) remains the open decision** (see D6 / R12);
  no correctness gap today — a legacy templated condition round-trips, installs legacy, runs via the legacy
  path, and the publish gate accepts a usable legacy operator. **D6 now LOCKED = no auto-migration** (the
  type-inference loss makes publish/import/first-run migration unsafe for ref operands; editor-Save with
  human type review stays the only migration). So **R9 is fully resolved.**
- **R10** — *(FIX, sequencing)* **Land the browser E2E** (real graph, node-click → live re-eval) before
  more phases — it's the only test exercising the headline UX; everything else mocks `@xyflow/react`.
- **R11** — *(DONE — exact-everywhere, user-chosen)* **Epsilon vs trichotomy.** Resolved: **epsilon removed
  entirely.** `eq`/`ne`/`gt`/`gte`/`lt`/`lte` and numeric membership now use exact IEEE-double `==`/`<`/`>`
  in both evaluators (`conditionEval.ts` + `ConditionEvaluator.cs`; the `eps`/`Eps` helpers deleted), so
  trichotomy holds (no value is both `gt` and `lte` at a boundary). Spec §5.1/§8 updated; the shared
  conformance fixture's `eq-num-num-within-eps` (was `True` via epsilon) is now `eq-num-num-tiny-diff-exact`
  → `False`, flowing through both FE and BE conformance suites. Trade-off documented: derived floats that
  drift (`0.1+0.2`) won't be `eq` — the standard FP caveat, modeled with a range. FE 197 / BE 162 green.
- **R12** — *(DONE, note)* **D6 third anchor = migrate-on-publish.** The gate already runs at
  publish/activate — a natural migration point that avoids eager import-time work and avoids carrying
  un-migrated legacy logic into a published-but-never-run workflow. (Added to D6's option set.)
- **R13** — *(DONE — verified, no code change)* **B5 ↔ D7 coherence.** Confirmed: `logic` is
  `Expression:false`, so `EvaluatePropertyValue` (the ref→raw-value substitution B5 worried about) **never
  runs over condition operands**. `ConditionLogicParser` reads each operand's `type` straight from the
  persisted JSON (`ConditionLogicParser.cs:144`) and the task reads `operand.Type` off the parsed model —
  type can't be stripped because the operand is never expression-resolved. B5 is structurally subsumed by
  D7; there is **no redundant type-preservation shim** guarding the dead `EvaluatePropertyValue` path to
  remove (the parser's type read is the legitimate, load-bearing code, not a guard).

## Operator safety
- `FIX` Server-side **regex timeout (ReDoS)** + max regex length (also in schema validation).
- ~~`FIX` **R8** — FE preview regex execution needs a deadline/worker or backend round-trip~~ — **DONE**
  via a synchronous catastrophic-shape guard in `conditionEval.ts` (refuses to execute; no editor freeze).

## Process reminder (user's standing instruction)
Implement **step by step**, **unit tests for every feature**, pure-helper + integration-test split.
**Ask rather than assume on UX forks / open DECIDE items.**
