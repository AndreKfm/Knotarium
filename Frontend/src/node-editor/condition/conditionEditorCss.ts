// Scoped styles for the Condition editor, injected via an inline <style> (the NodeEditorShell
// precedent). Values come from the design handoff README "Design Tokens" — the condition editor has
// its own palette (string=green, number=teal, boolean=amber, TRUE=green, FALSE=red) distinct from the
// app's generic node colors. Everything is scoped under `.cne-root` so it can't leak.

export const CONDITION_EDITOR_CSS = `
.cne-root {
  --cne-bg: #0a0e16; --cne-canvas: #090c13; --cne-surface: #0f141d; --cne-surface-2: #131922;
  --cne-ink: #e6edf3; --cne-muted: #8593a6; --cne-faint: #5d6675; --cne-line: #212b39;
  --cne-violet: #7c6cf0; --cne-amber: #f0b429; --cne-green: #34d399; --cne-red: #f0556d;
  position: absolute; inset: 0; display: flex; flex-direction: column;
  background: var(--cne-bg); color: var(--cne-ink);
  font-family: "Inter", system-ui, -apple-system, "Segoe UI", sans-serif;
}
.cne-amber { color: var(--cne-amber); }

.cne-topbar {
  height: 54px; flex: 0 0 54px; display: flex; align-items: center; justify-content: space-between;
  gap: 12px; padding: 0 16px; background: #0b0f17; border-bottom: 1px solid var(--cne-line);
}
.cne-back {
  display: inline-flex; align-items: center; gap: 6px; background: transparent; border: none;
  color: var(--cne-muted); cursor: pointer; font-size: 13px;
}
.cne-topbar-title { display: inline-flex; align-items: center; gap: 7px; font-weight: 700; font-size: 14px; }
.cne-save {
  display: inline-flex; align-items: center; gap: 7px; padding: 8px 14px; border-radius: 8px; border: none;
  color: #eafff5; font-weight: 700; font-size: 13px; cursor: pointer;
  background: linear-gradient(180deg, #2bbd7e, #1ea66c); box-shadow: 0 6px 18px -6px rgba(30,166,108,0.5);
}
.cne-save:disabled { opacity: 0.45; cursor: not-allowed; box-shadow: none; }

.cne-board {
  position: relative; flex: 1 1 auto; min-height: 0;
  background:
    radial-gradient(circle at 1px 1px, #161f2c 1px, transparent 0) 0 0 / 28px 28px,
    var(--cne-canvas);
}

.cne-toolbar {
  position: absolute; top: 12px; left: 16px; right: 16px; z-index: 5; display: flex;
  align-items: center; justify-content: space-between; gap: 12px; pointer-events: none;
}
.cne-toolbar-left { display: flex; align-items: center; gap: 8px; }
.cne-toolbar-title { font-weight: 700; font-size: 14px; }
.cne-hint { color: var(--cne-faint); font-size: 12px; }
.cne-toolbar-right { display: flex; align-items: center; gap: 10px; pointer-events: auto; }

.cne-segment { display: inline-flex; border: 1px solid var(--cne-line); border-radius: 8px; overflow: hidden; }
.cne-segment button {
  background: var(--cne-surface); border: none; color: var(--cne-muted);
  padding: 6px 12px; font-size: 12px; font-weight: 700; cursor: pointer;
}
.cne-segment button.cne-seg-on { background: rgba(124,108,240,0.18); color: #a99bff; }

.cne-source { display: inline-flex; align-items: center; gap: 8px; }
.cne-source-meta { display: inline-flex; align-items: center; gap: 6px; color: var(--cne-faint); font-size: 11px; }
.cne-stale {
  padding: 1px 6px; border-radius: 999px; font-size: 10px; font-weight: 800; text-transform: uppercase;
  color: var(--cne-amber); background: rgba(240,180,41,0.14); border: 1px solid rgba(240,180,41,0.4);
}

.cne-add {
  display: inline-flex; align-items: center; gap: 8px; padding: 10px 18px; border-radius: 10px;
  border: 1px solid rgba(124,108,240,0.55); background: rgba(124,108,240,0.2); color: #d2c9ff;
  font-size: 13.5px; font-weight: 700; cursor: pointer; transition: background 0.12s, border-color 0.12s;
}
.cne-add:hover { background: rgba(124,108,240,0.32); border-color: rgba(124,108,240,0.85); }
.cne-add svg { width: 17px; height: 17px; }

.cne-pill {
  padding: 6px 12px; border-radius: 999px; font-size: 12px; border: 1px solid var(--cne-line);
  background: var(--cne-surface); color: var(--cne-muted);
}
.cne-pill strong { font-family: ui-monospace, monospace; }
.cne-pill-true { border-color: rgba(52,211,153,0.5); } .cne-pill-true strong { color: var(--cne-green); }
.cne-pill-false { border-color: rgba(240,85,109,0.5); } .cne-pill-false strong { color: var(--cne-red); }
.cne-pill-error { border-color: rgba(240,85,109,0.5); } .cne-pill-error strong { color: var(--cne-red); }
.cne-pill-neutral strong { color: var(--cne-faint); }

/* React Flow chrome resets — our node *types* are named 'input'/'output', which collide with React
   Flow's built-in node types, so the stock stylesheet paints a white box (bg + border + padding)
   behind them. Strip the default node chrome; our components own all visuals. */
.cne-root .react-flow__node {
  background: transparent; border: none; border-radius: 0; padding: 0;
  width: auto; color: inherit; font-size: inherit; box-shadow: none;
}
.cne-root .react-flow__handle {
  width: 7px; height: 7px; min-width: 0; min-height: 0;
  background: var(--cne-line); border: 1px solid var(--cne-canvas); opacity: 0.7;
}

/* Nodes */
.cne-input {
  display: flex; align-items: center; gap: 7px; width: 204px; height: 42px; padding: 0 12px;
  border-radius: 14px; background: linear-gradient(180deg, #121822, #0f141d); border: 1px solid var(--cne-line);
  font-size: 12.5px;
}
.cne-diamond { width: 9px; height: 9px; border-radius: 2px; transform: rotate(45deg); flex: 0 0 auto; }
/* min-width:0 lets the label actually shrink + ellipsize inside the flex row (a flex item won't go
   below its content's intrinsic width without it). */
.cne-input-label { flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.cne-lit-eq { color: var(--cne-amber); font-family: ui-monospace, monospace; margin-right: 4px; }
/* The value badge is capped + ellipsized so a long list literal ("4, 1, 2, …") truncates instead of
   spilling past the fixed-width card; the full value stays available via the title tooltip. */
.cne-input-badge {
  font-family: ui-monospace, monospace; font-size: 11px; padding: 2px 6px; border-radius: 6px;
  background: #0a0f17; border: 1px solid var(--cne-line); flex: 0 1 auto; min-width: 0; max-width: 92px;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}

.cne-cmp {
  position: relative; width: 150px; min-height: 118px; box-sizing: border-box; padding: 8px;
  display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 10px;
  border-radius: 14px;
  background: linear-gradient(180deg, rgba(240,180,41,0.06), #0f141d); border: 1px solid rgba(240,180,41,0.4);
}
/* Per-leaf evaluation error, shown under the chip so a red 'error' status isn't a dead end. The card
   uses min-height, so this line grows it (the editor measures real sizes and re-lays-out). */
.cne-cmp-error {
  max-width: 134px; font-size: 10px; line-height: 1.3; color: var(--cne-red); text-align: center;
  word-break: break-word;
}
.cne-cmp-remove {
  position: absolute; top: 6px; right: 6px; background: transparent; border: none; color: var(--cne-faint);
  cursor: pointer; opacity: 0; transition: opacity 0.15s;
}
.cne-cmp:hover .cne-cmp-remove { opacity: 1; }
.cne-cmp-remove:disabled { opacity: 0 !important; cursor: not-allowed; }
.cne-op-pill {
  display: inline-flex; align-items: center; gap: 6px; max-width: 130px; padding: 5px 9px; border-radius: 9px;
  background: var(--cne-surface-2); font-size: 12px; font-weight: 600;
}
.cne-op-symbol {
  display: inline-flex; align-items: center; justify-content: center; min-width: 18px; height: 18px;
  border-radius: 5px; background: rgba(240,180,41,0.18); color: var(--cne-amber); font-weight: 800;
}
.cne-op-symbol-sm { min-width: 15px; height: 15px; font-size: 10px; }
.cne-op-label { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

.cne-chip { font-family: ui-monospace, monospace; font-size: 11.5px; font-weight: 700; padding: 2px 8px; border-radius: 999px; }
.cne-chip-true { color: var(--cne-green); background: rgba(52,211,153,0.12); }
.cne-chip-false { color: var(--cne-red); background: rgba(240,85,109,0.12); }
.cne-chip-neutral { color: var(--cne-faint); background: rgba(133,147,166,0.1); }

.cne-comb {
  display: flex; flex-direction: column; align-items: center; gap: 8px; width: 168px; padding: 12px;
  border-radius: 14px; background: linear-gradient(180deg, rgba(124,108,240,0.07), #0f141d);
  border: 1px solid rgba(124,108,240,0.4);
}
.cne-comb-head { font-weight: 800; font-size: 12px; color: #a99bff; letter-spacing: 0.04em; }
.cne-comb-rows { display: flex; flex-direction: column; gap: 4px; width: 100%; }
.cne-comb-row { display: flex; align-items: center; justify-content: space-between; }
.cne-tf { font-family: ui-monospace, monospace; font-size: 10px; font-weight: 800; }
.cne-tf-t { color: var(--cne-green); } .cne-tf-f { color: var(--cne-red); } .cne-tf-n { color: var(--cne-faint); }

.cne-out {
  display: flex; flex-direction: column; gap: 6px; width: 210px; padding: 12px; border-radius: 14px;
  background: linear-gradient(180deg, #121822, #0f141d); border: 1px solid var(--cne-line);
}
.cne-out-title { font-weight: 700; font-size: 12.5px; }
.cne-branch {
  display: flex; align-items: center; gap: 8px; padding: 6px 8px; border-radius: 8px; font-weight: 700;
  font-size: 12px; border: 1px solid transparent; color: var(--cne-muted);
}
.cne-branch-dot { width: 8px; height: 8px; border-radius: 999px; background: currentColor; }
.cne-branch-true { color: var(--cne-green); }
.cne-branch-false { color: var(--cne-red); }
.cne-branch-hot.cne-branch-true { background: rgba(52,211,153,0.1); border-color: var(--cne-green); box-shadow: 0 0 18px -4px rgba(52,211,153,0.6); }
.cne-branch-hot.cne-branch-false { background: rgba(240,85,109,0.1); border-color: var(--cne-red); box-shadow: 0 0 18px -4px rgba(240,85,109,0.6); }

/* Empty-first placeholder (the "Add condition / click a variable" call to action). */
.cne-placeholder { position: relative; }
.cne-placeholder-btn {
  display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 8px;
  width: 210px; height: 150px; padding: 16px; border-radius: 16px; cursor: pointer;
  border: 1.5px dashed var(--cne-line); background: rgba(124,108,240,0.03); color: var(--cne-muted);
  transition: border-color 0.15s, background 0.15s;
}
.cne-placeholder-btn:hover { border-color: rgba(124,108,240,0.55); background: rgba(124,108,240,0.08); }
.cne-placeholder-plus {
  display: inline-flex; align-items: center; justify-content: center; width: 40px; height: 40px;
  border-radius: 12px; background: rgba(124,108,240,0.18); color: #a99bff;
}
.cne-placeholder-title { font-weight: 700; font-size: 14px; color: var(--cne-ink); }
.cne-placeholder-sub { font-size: 12px; color: var(--cne-faint); }

/* The input node is a button; reset native styling. */
.cne-input-wrap { position: relative; }
.cne-input { width: 204px; cursor: pointer; color: inherit; text-align: left; }
.cne-op-pill { border: none; cursor: pointer; color: inherit; }
.cne-op-caret { color: var(--cne-faint); margin-left: 2px; }

/* Popovers (operator menu + input editor). Left-anchored to the trigger so they open rightward and
   stay on screen even when the operand sits near the left edge (centering used to clip off-screen). */
.cne-menu, .cne-ied {
  position: absolute; left: 0; top: calc(100% + 8px); z-index: 20;
  width: 264px; max-width: calc(100vw - 32px); background: var(--cne-surface); border: 1px solid var(--cne-line); border-radius: 12px;
  box-shadow: 0 40px 90px -28px rgba(0,0,0,0.95); padding: 8px; animation: cneRise 0.12s ease-out;
}
@keyframes cneRise { from { opacity: 0; transform: translateY(-6px); } to { opacity: 1; transform: translateY(0); } }

.cne-menu-search { display: flex; align-items: center; gap: 6px; padding: 4px; }
.cne-menu-search input {
  flex: 1 1 auto; background: #0a0f17; border: 1px solid var(--cne-line); border-radius: 8px;
  color: var(--cne-ink); padding: 6px 8px; font-size: 12px; outline: none;
}
.cne-menu-typetag { font-size: 10px; color: var(--cne-muted); padding: 2px 6px; border: 1px solid var(--cne-line); border-radius: 6px; }
.cne-menu-list { max-height: 260px; overflow-y: auto; }
.cne-menu-grouphdr { font-size: 10px; font-weight: 700; text-transform: uppercase; color: var(--cne-faint); padding: 8px 6px 4px; letter-spacing: 0.05em; }
.cne-menu-row {
  display: flex; align-items: center; gap: 8px; width: 100%; padding: 6px 8px; border: none; border-radius: 8px;
  background: transparent; color: var(--cne-ink); font-size: 12px; cursor: pointer; text-align: left;
}
.cne-menu-row:hover:not(:disabled) { background: var(--cne-surface-3, #18202c); }
.cne-menu-row:disabled { opacity: 0.4; cursor: not-allowed; }
.cne-menu-row-on { background: rgba(124,108,240,0.1); }
.cne-menu-label { flex: 1 1 auto; }
.cne-menu-unary { font-size: 10px; font-style: italic; color: var(--cne-muted); }
.cne-menu-check { color: var(--cne-violet); }
.cne-menu-empty, .cne-ied-empty { padding: 12px; font-size: 12px; color: var(--cne-muted); text-align: center; }
.cne-menu-hint { margin-top: 6px; padding: 6px 8px; font-size: 11px; color: var(--cne-amber); background: rgba(240,180,41,0.08); border-radius: 8px; }

.cne-ied-tabs { display: flex; gap: 4px; margin-bottom: 8px; }
.cne-ied-tabs button {
  flex: 1; background: var(--cne-surface-2); border: 1px solid var(--cne-line); border-radius: 8px;
  color: var(--cne-muted); padding: 6px; font-size: 12px; font-weight: 600; cursor: pointer;
}
.cne-ied-tabs button.cne-tab-on { background: rgba(124,108,240,0.15); color: #a99bff; border-color: rgba(124,108,240,0.4); }
.cne-ied-reflist { list-style: none; margin: 0; padding: 0; max-height: 200px; overflow-y: auto; }
.cne-ied-refrow {
  display: flex; align-items: center; gap: 8px; width: 100%; padding: 6px 8px; border: none; border-radius: 8px;
  background: transparent; color: var(--cne-ink); font-size: 12px; cursor: pointer; text-align: left;
}
.cne-ied-refrow:hover { background: var(--cne-surface-3, #18202c); }
.cne-ied-refrow-on { background: rgba(124,108,240,0.1); }
.cne-ied-refpath { flex: 1 1 auto; font-family: ui-monospace, monospace; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.cne-ied-reftype { font-size: 10px; color: var(--cne-muted); }
.cne-ied-sample { display: flex; flex-direction: column; gap: 4px; margin-top: 8px; font-size: 11px; color: var(--cne-muted); }
.cne-ied-sample input, .cne-ied-litinput {
  background: #0a0f17; border: 1px solid var(--cne-line); border-radius: 8px; color: var(--cne-ink);
  padding: 6px 8px; font-size: 12px; outline: none; width: 100%;
}
.cne-ied-typeseg { margin-bottom: 8px; width: 100%; }
.cne-ied-typeseg button, .cne-ied-lit .cne-segment button { flex: 1; }
.cne-ied-listhint { margin-top: 6px; font-size: 11px; line-height: 1.35; color: var(--cne-muted); }
.cne-diamond[data-type="string"] { background: var(--cne-green); }
.cne-diamond[data-type="number"] { background: var(--cne-teal, #22d3ee); }
.cne-diamond[data-type="boolean"] { background: var(--cne-amber); }
`;
