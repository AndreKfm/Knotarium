# Plan — Keyed / Nested Path Access for Variables

Branch: `feat/dynamic-options-resource-locator` (or split into its own branch — see note at end)

Goal: let authors treat a variable that holds an object/array as an **associative structure**, reading
*and* writing nested members by name or index — `myDict["name"]`, `myDict.name`, `list[0]`,
`config.servers[2].host` — from both the **Set Variable node** (write) and **`{{ }}` expressions** (read).

Today a variable *value* can already be an object/array (`object` in `IDictionary<string, object>`,
typically a `JsonElement`), but:
- **Reads** never navigate into it. `$variables.foo.bar` is treated as a single variable literally named
  `"foo.bar"` — [ExpressionEvaluator.cs:551](Backend/KnotGarden.NodeRuntime/ExpressionEvaluator.cs#L551).
- **Writes** only set a whole value under a flat name — [SetVariableNodeTask.cs:11-16](Backend/KnotGarden.Features/Nodes/SetVariableNodeTask.cs#L11).
- `NavigateJson` (used for **node outputs**) supports dotted paths but **only integer** bracket indexing,
  not string keys — [ExpressionEvaluator.cs:642](Backend/KnotGarden.NodeRuntime/ExpressionEvaluator.cs#L642).

---

## Non-goals
- No new templating language; we extend the existing `{{ }}` / path syntax.
- No change to the Inline Code path — scripts already do native indexing (`dict["name"] = 1`); unaffected.
- Not adding computed/dynamic keys (`foo[$bar]`) in v1 — keys are literal names or integer indices.

---

## Design

### Shared path grammar + parser
A single tokenizer used by **both** read and write so syntax never diverges. Accept:
- `.name` — member access
- `["name"]` / `['name']` — member access (lets keys contain dots/spaces)
- `[0]` — array index (non-negative integer)

Parse a path string into an ordered list of segments `Segment = Member(string) | Index(int)`.
The head segment is the variable name; the tail is the navigation path. New file
`Backend/KnotGarden.NodeRuntime/VariablePath.cs` (parser is pure, unit-testable, shared by read & write).

Rejects/edge cases the parser defines once: empty segment, unterminated bracket, non-integer inside `[…]`
without quotes → treat as parse error surfaced to the caller (null on read, node failure on write).

### Part A — Reads (expressions)
Two changes in `ExpressionEvaluator`:

1. **String-key bracket support in `NavigateJson`** ([:653-662](Backend/KnotGarden.NodeRuntime/ExpressionEvaluator.cs#L653)):
   when the bracket body isn't an int, strip surrounding quotes and treat it as a **property name** on an
   `Object`. Keep the existing int path for `Array`. (Benefits node-output navigation too, e.g.
   `$node.x.output.map["key"]`.)
2. **Navigate into variables** ([:551-555](Backend/KnotGarden.NodeRuntime/ExpressionEvaluator.cs#L551)):
   split the `$variables.` reference into head + remaining path via the shared parser; `GetVariable<object>(head)`;
   if there's a remaining path and the value is a `JsonElement`, run `NavigateJson` and `ConvertJsonElement` the
   result (mirrors how `$node.…output.…` already works at [:539-543](Backend/KnotGarden.NodeRuntime/ExpressionEvaluator.cs#L539)).
   Bare `$variables.foo` with no path keeps current behavior.

Missing member / out-of-range index / indexing a non-container → `null` (consistent with current
`NavigateJson` miss behavior). Reads never throw.

### Part B — Writes (Set Variable node)
`SetVariableNodeTask` ([:11-16](Backend/KnotGarden.Features/Nodes/SetVariableNodeTask.cs#L11)) currently does
`Variables.Set(flatName, value)`. New behavior:

1. Parse `variableName` with the shared parser → head + path segments.
2. **No path** → unchanged (`Variables.Set(head, value)`), so existing workflows are byte-for-byte identical.
3. **With a path** → deep-set:
   - Load the current head variable. Because `JsonElement` is **immutable**, materialize it into a mutable
     tree (`Dictionary<string, object?>` for objects, `List<object?>` for arrays) via a new
     `ToMutable(JsonElement)` helper. If the variable is absent/null, start from an empty container whose
     **type is chosen by the first segment** — `Member` → object, `Index` → array (auto-vivification).
   - Walk the segments, auto-creating missing intermediate containers (next segment's kind decides object vs
     array). Set the leaf to the incoming `value` (run through `JsonToClr` so it stores consistently).
   - `Variables.Set(head, mutatedTree)`. Siblings are preserved (leaf-set, not whole-replace).

Write failure modes → **node failure** with a clear message (this is design-authored intent, fail loud, unlike
read's silent null):
- Type conflict: path expects an object but the existing value at that segment is a scalar/array (or vice
  versa). Report the path and the conflict.
- `Index` write to an existing object, or `Member` write to an existing array.
- Out-of-range positive index on write: **auto-grow, padding gaps with null** (e.g. `array[1]` on an empty
  array yields `[null, value]`), mirroring JS array assignment. (Originally specced as append-only at
  `index == length`; relaxed so authors can write any index without first filling the lower slots.)

Helpers live in a small `VariableTree` utility (materialize / deep-set), kept next to `VariablePath` so both
read and write share the segment model.

### Frontend
- **Set Variable node form**: the `variableName` field already takes free text — no schema change needed; path
  syntax just works. Add inline helper text / placeholder showing `myDict["name"]` and `list[0]` so it's
  discoverable. Optional: lightweight client-side validation reusing the same grammar (nice-to-have, not required).
- **Expression editor / autocomplete**: variable suggestions stay name-level; nested keys aren't known at design
  time (values resolve at run). No change required for v1.

---

## Phased build order
1. `VariablePath` parser + `VariableTree` (materialize / deep-set) helpers, fully unit-tested in isolation.
2. **Reads**: string-key brackets in `NavigateJson` + variable navigation in the evaluator. Tests:
   `$variables.d["name"]`, `$variables.d.name`, `$variables.list[0]`, deep `a.b[1].c`, misses → null.
   Regression: bare `$variables.foo`, integer node-output indexing still pass.
3. **Writes**: path-aware `SetVariableNodeTask` with auto-vivification + materialize-on-write. Tests:
   create-from-absent, nested create, leaf overwrite preserves siblings, array append-at-length,
   type-conflict → failure, index-on-object → failure.
4. Frontend placeholder/helper text on the Set Variable field (+ optional client-side grammar validation).

## Acceptance criteria
- `{{ $variables.myDict["name"] }}` and `{{ $variables.myDict.name }}` both read a nested member.
- `{{ $variables.list[0] }}` reads by index; out-of-range / missing key → empty (null), no throw.
- Set Variable with `variableName = myDict["name"]`, `value = 1` creates/updates that key, leaving other keys
  intact; re-reading the whole `myDict` shows both old and new keys.
- Set Variable into an absent variable auto-creates the container (object for a name segment, array for an index).
- A path write that conflicts with an existing scalar/array type fails the node with a clear journal message.
- Existing flat-name Set Variable and `$variables.foo` reads are unchanged (regression-covered).
- Inline Code variable access is untouched.

## Open decisions (sensible defaults chosen; flag if you disagree)
- **Array write out-of-range** → auto-grow, padding gaps with `null` (JS-like). (Relaxed from the original
  append-only-at-`index == length` decision.)
- **Auto-vivification on write** → enabled (create missing intermediates). Default; the alternative (require the
  parent to pre-exist) is stricter but more friction for authors.
- **Read miss** → null/empty, never throws. Matches current `NavigateJson`.
- **Computed keys** (`foo[$other]`) → out of scope for v1.

## Branch note
This is independent of the resource-locator/dynamic-options work. Cleanest as its own branch
(`feat/variable-keyed-path-access`) off `main` so the two features review and merge separately. Currently
drafted on the dynamic-options branch only because that's where the conversation is.
