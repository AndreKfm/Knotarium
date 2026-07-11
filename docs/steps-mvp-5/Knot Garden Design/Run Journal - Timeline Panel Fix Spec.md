# Run Journal — Timeline Panel: Fix Spec

This is a correction list for the current build. The timeline skeleton is right
(node rail, discs, expandable output, elapsed deltas) — keep it. Fix the items
below. Reference mock: `Run Journal — Timeline.html`.

---

## P0 — Blocking (do these first)

### 1. Remove the old `NODE RESULTS` cards entirely
The legacy `NODE RESULTS` list (raw IDs + `Pending` badges) still renders above
the new timeline and contradicts it — it shows nodes as `Pending` while the
timeline shows them `Completed`.

- Before: two stacked panels showing the same nodes with conflicting status.
- After: the timeline is the only per-node view. Delete `NODE RESULTS`.
  The `SCHEDULE CONTROLS` card (Active / Fire now / next fire) stays — that is
  scheduler config, not a run result.

### 2. Show the trigger node's friendly name as a header
Every node has a name header (`Log`, `End`) except the trigger, which falls back
to the raw ID inside a sentence.

- Before: `Trigger node 'scheduler-ldr0aa924' activated.` with no header.
- After: header reads `Cron Scheduler`, with `scheduler-ldr0aa924` as the muted
  monospace subline — identical structure to every other node.
  `Trigger node activated.` becomes an event line inside the expanded detail,
  not the node title.

### 3. Fix event order inside an expanded node
Events currently render out of sequence and the output block floats above them.

- Before: `OUTPUT (message)` -> `+48ms STARTED` -> `+65ms DONE`
  Result appears before the event that produced it, and timestamps jump around.
- After: chronological top-to-bottom:
  1. `+48ms · STARTED · Executing node (type 'log').`
  2. `+65ms · DONE · [LOG] log message`
  3. Then the `OUTPUT` block last, or pinned as a collapsed summary.
  Never interleave output before its own `STARTED` event.
- Use one timestamp model only. Keep relative elapsed (`+0ms`, `+48ms`) and
  drop the absolute `[17:57:13]` clock.

---

## P1 — The point of the redesign

### 4. Differentiate AUTO vs MANUAL triggers
The run gives no signal for how it started. This is a core requirement.

- After: render a trigger banner directly under the panel header.
- `MANUAL`: violet badge `MANUAL`, text `Fired manually — "Fire now" · 17:57:13`
- `AUTO`: cyan badge `AUTO`, text `Triggered by schedule · */5 * * * * · next 18:00:00`
- Optionally mirror a small `AUTO` / `MANUAL` chip on the `Cron Scheduler` node.

### 5. Support all node states, not just Completed
The rail must visually distinguish these states:

- `completed`: green check.
- `running`: cyan pulsing disc + spinner + animated connector into next node.
- `pending`: hollow gray ring, no glow.
- `failed`: red X disc, red error block.
- `skipped`: faded row, dashed dead connector.

Connector segment color encodes flow:

- solid green = both passed
- animated cyan dashes = running into next
- dashed gray = skipped or after failure
- flat gray = pending

---

## P2 — Polish

### 6. Rail alignment & spacing
- Vertically center each disc to its node header row, not the top of a tall card.
- Tighten card padding and keep consistent vertical rhythm between nodes.
- Connector line should span disc-to-disc and not overrun.

### 7. Kill the visual glitches
- Remove the stray highlighted / selected span on `xjvuunpkv` mid-sentence.
- Fix the clipped top `Trigger` card by adding the right top padding in the
  scroll container.
- Stop the map / satellite image and `React Flow` watermark from bleeding
  through at the bottom. The journal panel must sit on an opaque surface above
  the canvas.

### 8. Stop repeating the node ID
Do not print the ID both in a sentence and as the subline. Name is the header,
ID is the muted subline, once.

---

## Node Row Anatomy (Target)

```text
┌─ disc ─┐   Cron Scheduler            [AUTO]  COMPLETED   +0ms   ⌄
│  ◷ ✓  │   scheduler-ldr0aa924
│  │    │   ── expanded ──────────────────────────────────
│  │    │   +0ms · DONE · Trigger node activated.
│  │    │   ┌ TRIGGERED AT ─────────────────────────────┐
│  │    │   │ 2026-05-31T15:57:13.6167+00:00            │
└──┴────┘   └───────────────────────────────────────────┘
```

Header = friendly name (primary) + muted ID (secondary) + state pill + elapsed.
Detail on expand = chronological events, then the output / payload block.

---

# Run Journal — Timeline Panel: Round 2 Punch List

Round 1 landed well — friendly names, the AUTO/MANUAL banner + node chip, and
chronological events are all correct. Five items remain. **#1 is behavioral and
is the most important — it's the whole point of the panel.** The rest are
truthfulness + layout bugs.

Reference mock (shows all five done correctly): `Run Journal — Timeline.html`.

---

## 1. Collapse nodes by default — one summary line each  ⟵ TOP PRIORITY

Right now **every node is permanently expanded**: each shows its full event list
*and* its OUTPUT block, always. That's still a wall of text — you can only see
~1.5 nodes and you scroll forever. This defeats the "details on demand" goal.

**Default (collapsed) = exactly one row per node:**

```text
✓  Log   COMPLETED   +367ms          "log message"   ⌄
   log-xjvuunpkv
```

- Left: state mark + **friendly name** + state pill.
- Right: elapsed + a **one-line summary** + chevron.
- The **summary is the useful bit of the output**, shown without expanding:
  - Cron Scheduler → `Schedule */5 * * * *`
  - Log → `"log message"`
  - HTTP Request → `GET /api/orders → 200`
  - Transform → `18 → 18 items`
  - End → `End execution`
- The muted ID sits on the second line under the name. Nothing else when collapsed.

**Expanded (on click) = the detail we already built:**
chronological events (`STARTED → DONE`) **then** the OUTPUT / payload block.

**Behavior:**
- All nodes **collapsed by default.** Click a node (or its chevron) to expand;
  click again to collapse. Independent per node.
- A failed node may auto-expand to surface the error — that's the one exception.
- Result: the entire 3-node run fits in one screen; you drill into one node only
  when you need to.

| | Current build | Target |
|---|---|---|
| Default state | everything expanded | everything **collapsed** |
| Footprint per node | ~6 lines + boxes | **1 line** + ID subline |
| OUTPUT box | always visible | on expand only |
| Whole run visible | ~1.5 nodes | all nodes at once |

---

## 2. Header status must match the nodes
The panel header shows **`Pending`** while both visible nodes are **`Completed`**.
That's the old contradiction, relocated to the header.

- If the run is still executing → header reads **`RUNNING`** (cyan, pulsing).
- If all nodes finished OK → **`SUCCESS`** (green).
- If any node failed → **`FAILED`** (red).
- Never `Pending` when nodes beneath it are already done.

## 3. Un-clip the OUTPUT value
`2026-06-01T17:21:24.862+00:0…` is cut off by the block's right edge.

- Payload block needs `white-space: pre-wrap; overflow-wrap: anywhere;`
  (or horizontal scroll). The most useful detail must be fully readable.

## 4. Left-align the event rows
Event text ("Trigger node activated.", "Executing node…") is shoved to the right
on a second indented line, breaking the rail rhythm.

- Put the whole event on **one baseline-aligned row**:
  `+367ms · ● · DONE · [LOG] log message` — timestamp, dot, tag, and text share
  one line, all left-aligned to a consistent column. No second-line indent.

## 5. Trigger elapsed = `+0ms`
The Cron Scheduler shows `+314ms`. The trigger is the **start** of the run, so its
elapsed-from-start is `+0ms`. (You're likely showing wall-clock offset instead of
elapsed-from-run-start — normalize every node's delta to run start = 0.)

---

### Priority order
1. Collapse-by-default + summary line (behavioral core)
2. Header status truthfulness
3. OUTPUT clipping
4. Event-row alignment
5. Trigger elapsed normalization