// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Styles for the v2 flow editor's NEW node kinds (group + not) and the per-node action tools.
// Complements CONDITION_EDITOR_CSS (input/comparator/output cards, op pill, chips, popovers). Loaded
// after it so these win where they overlap.

export const CONDITION_TREE_CSS = `
/* React Flow makes a node pointer-events:none (inline) when it is non-draggable + non-selectable +
   non-connectable, so the canvas can pan "through" it — but our nodes are interactive, so clicks must
   reach them. Force pointer-events on; the nodrag/nopan classes on each node keep drag/pan off it. */
.cne-root .react-flow__node { pointer-events: all !important; }

/* Edge labels: a dark pill, never React Flow's default white rect. */
.cne-root .react-flow__edge-textbg { fill: #0e1622 !important; }
.cne-root .react-flow__edge-text { fill: #9fb0c3 !important; }

/* Per-node action tools (wrap / negate / delete), revealed on hover. */
.cne-node-tools, .cne-gnode-tools {
  position: absolute; top: 6px; right: 6px; display: inline-flex; gap: 3px; opacity: 0;
  transition: opacity 0.12s; z-index: 3;
}
.cne-cmp:hover .cne-node-tools, .cne-gnode:hover .cne-gnode-tools { opacity: 1; }
.cne-node-tools button, .cne-gnode-tools button, .cne-nnode-head button {
  font: 700 9px ui-monospace, monospace; color: var(--cne-faint, #8593a6);
  background: rgba(10,14,20,0.6); border: 1px solid rgba(36,49,68,0.6); border-radius: 5px;
  padding: 2px 5px; cursor: pointer; display: inline-flex; align-items: center;
}
.cne-node-tools button:hover, .cne-gnode-tools button:hover, .cne-nnode-head button:hover {
  color: #d7e2f0; border-color: #3a4a60;
}

/* AND/OR group node (violet). */
.cne-gnode {
  position: relative; width: 152px; box-sizing: border-box; display: flex; flex-direction: column;
  align-items: center; gap: 7px; padding: 12px 12px 10px; border-radius: 14px;
  background: linear-gradient(180deg, rgba(124,108,240,0.16), rgba(124,108,240,0.05));
  border: 1px solid rgba(124,108,240,0.5);
}
/* Operator hero: the active combinator is a large glowing word (violet AND / sky OR); a small caption
   below makes the AND↔OR switch discoverable. Both are buttons that toggle. */
.cne-gnode-op { width: 100%; display: flex; flex-direction: column; align-items: center; gap: 1px; }
.cne-op-hero {
  font: 800 22px system-ui, sans-serif; letter-spacing: 0.02em; line-height: 1.1;
  background: none; border: none; padding: 0 4px; cursor: pointer;
}
.cne-op-and { color: #b3a4ff; text-shadow: 0 0 14px rgba(124,108,240,0.6); }
.cne-op-or  { color: #5ec5f5; text-shadow: 0 0 14px rgba(56,189,248,0.55); }
.cne-op-hero:hover { filter: brightness(1.12); }
.cne-op-switch {
  font: 700 9.5px ui-monospace, monospace; letter-spacing: 0.04em; color: #6b7888;
  background: none; border: none; padding: 1px 4px; cursor: pointer; border-radius: 5px;
}
.cne-op-switch:hover { color: #aeb9c8; background: rgba(255,255,255,0.05); }
.cne-gnode-add { display: flex; gap: 6px; width: 100%; }
.cne-gnode-add button {
  flex: 1; display: inline-flex; align-items: center; justify-content: center; gap: 4px;
  font: 700 12.5px system-ui, sans-serif; color: #c4b8ff; background: rgba(124,108,240,0.16);
  border: 1px solid rgba(124,108,240,0.45); border-radius: 8px; padding: 6px 6px; cursor: pointer;
}
.cne-gnode-add button:hover { background: rgba(124,108,240,0.3); border-color: rgba(124,108,240,0.75); }

/* NOT node (red). */
.cne-nnode {
  position: relative; width: 108px; box-sizing: border-box; display: flex; flex-direction: column;
  align-items: center; gap: 7px; padding: 10px; border-radius: 12px;
  background: linear-gradient(180deg, rgba(240,85,109,0.14), rgba(240,85,109,0.04));
  border: 1px solid rgba(240,85,109,0.45);
}
.cne-nnode-head { display: inline-flex; align-items: center; gap: 6px; }
.cne-not-label {
  font: 700 11px ui-monospace, monospace; letter-spacing: 0.08em; color: #f0809a;
  padding: 2px 8px; border: 1px solid rgba(240,85,109,0.4); border-radius: 6px;
}

/* Plain-language summary strip above the graph: a labelled pill per comparator (green pass / red fail /
   neutral runtime) + the top-level combinator. Read-only — makes the condition legible at a glance. */
/* Floats below the (absolutely-positioned) toolbar rather than in normal flow, which overlapped it. */
/* top: pushed clear of the toolbar's action-button row (top:12 + ~40px tall) so the two bars don't
   crowd; min-height + roomier padding give the pills/operator chip breathing room (was a cramped 32px). */
.cne-summary {
  position: absolute; top: 72px; left: 16px; right: 16px; z-index: 4; pointer-events: auto;
  display: flex; align-items: center; flex-wrap: wrap; gap: 10px 12px;
  min-height: 46px; padding: 11px 16px; border-radius: 12px;
  border: 1px solid var(--cne-line, rgba(255,255,255,0.14)); background: rgba(13,18,26,0.92);
  backdrop-filter: blur(3px);
}
.cne-summary-k { font: 800 9.5px system-ui, sans-serif; letter-spacing: 0.12em; color: #6b7888; }
.cne-summary-pills { display: flex; flex-wrap: wrap; gap: 8px; }
.cne-summary-pill {
  display: inline-flex; align-items: center; gap: 7px; padding: 5px 11px; border-radius: 999px;
  font: 600 12px ui-monospace, monospace; border: 1px solid rgba(255,255,255,0.12);
  background: rgba(255,255,255,0.03); color: #cdd6e2;
}
.cne-summary-pill .cne-sp-letter { font-weight: 800; color: #8593a6; }
.cne-summary-pill .cne-sp-icon { font-weight: 800; }
.cne-sp-pass { border-color: rgba(52,211,153,0.45); background: rgba(52,211,153,0.10); color: #9beecb; }
.cne-sp-pass .cne-sp-letter, .cne-sp-pass .cne-sp-icon { color: #34d399; }
.cne-sp-fail { border-color: rgba(240,85,109,0.45); background: rgba(240,85,109,0.10); color: #f3a9b6; }
.cne-sp-fail .cne-sp-letter, .cne-sp-fail .cne-sp-icon { color: #f0556d; }
.cne-sp-error { border-color: rgba(240,85,109,0.6); background: rgba(240,85,109,0.14); color: #f0556d; }
.cne-sp-runtime, .cne-sp-neutral { color: #aeb9c8; }
.cne-summary-sep { width: 1px; height: 18px; background: var(--border-color, rgba(255,255,255,0.14)); }
.cne-summary-op {
  font: 800 13px system-ui, sans-serif; letter-spacing: 0.04em; padding: 3px 11px; border-radius: 8px;
}
.cne-summary-op.cne-op-and { color: #b3a4ff; background: rgba(124,108,240,0.16); border: 1px solid rgba(124,108,240,0.45); }
.cne-summary-op.cne-op-or  { color: #5ec5f5; background: rgba(56,189,248,0.14); border: 1px solid rgba(56,189,248,0.4); }

/* ── Test mode ── amber-framed bar + presets; floats below the toolbar like the summary. */
.cne-test-enter {
  font: 700 12px system-ui, sans-serif; color: #f0b429; background: rgba(240,180,41,0.12);
  border: 1px solid rgba(240,180,41,0.4); border-radius: 8px; padding: 5px 11px; cursor: pointer; pointer-events: auto;
}
.cne-test-enter:hover { background: rgba(240,180,41,0.2); }
.cne-testbar {
  position: absolute; top: 72px; left: 16px; right: 16px; z-index: 4; pointer-events: auto;
  display: flex; flex-direction: row; align-items: center; justify-content: space-between; gap: 14px;
  padding: 10px 14px; border-radius: 12px;
  border: 1px solid rgba(240,180,41,0.5); background: rgba(28,22,8,0.92); backdrop-filter: blur(3px);
}
.cne-testbar-main { display: flex; flex-direction: column; gap: 8px; min-width: 0; }
.cne-testbar-row { display: flex; align-items: center; gap: 10px; }
.cne-test-dot { width: 8px; height: 8px; border-radius: 50%; background: #f0b429; box-shadow: 0 0 0 3px rgba(240,180,41,0.2); flex: none; }
.cne-test-title { font: 800 11px system-ui, sans-serif; letter-spacing: 0.12em; color: #f0b429; }
.cne-test-sub { font: 600 11px system-ui, sans-serif; color: #b79a5a; }
.cne-test-exit {
  flex: none; align-self: center; display: inline-flex; align-items: center; gap: 5px;
  font: 700 12.5px system-ui, sans-serif; color: #f0c66a; background: rgba(240,180,41,0.10);
  border: 1px solid rgba(240,180,41,0.45); border-radius: 8px; padding: 7px 14px; cursor: pointer;
  transition: background 0.12s, border-color 0.12s;
}
.cne-test-exit:hover { background: rgba(240,180,41,0.2); border-color: rgba(240,180,41,0.7); }
.cne-test-presets { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
.cne-test-presets button {
  font: 600 11.5px system-ui, sans-serif; color: #d3dbe6; background: rgba(255,255,255,0.04);
  border: 1px solid var(--cne-line, rgba(255,255,255,0.14)); border-radius: 8px; padding: 5px 11px; cursor: pointer;
}
.cne-test-presets button:hover { background: rgba(255,255,255,0.09); border-color: rgba(255,255,255,0.25); }
.cne-test-presets .cne-test-clear { color: #8593a6; }

/* In test mode, a signal-ref input is editable (amber); a fixed comparand reads cyan/locked. */
.cne-input-test.cne-input-sig { border-color: rgba(240,180,41,0.55); }
.cne-input-test.cne-input-fixed { opacity: 0.85; }
/* Editable signal-value field: an obvious amber input with a pencil hint, roomy enough to read the value
   you type and to show the cursor (the old tiny right-aligned box looked like a mystery dot). */
.cne-test-fieldwrap {
  margin-left: auto; display: inline-flex; align-items: center; gap: 5px; cursor: text;
  background: rgba(240,180,41,0.12); border: 1px solid rgba(240,180,41,0.5); border-radius: 7px; padding: 3px 8px;
}
.cne-test-fieldwrap:focus-within { border-color: #f0b429; background: rgba(240,180,41,0.2); }
.cne-test-ico { color: #c79a3a; flex: none; }
.cne-test-field {
  width: 86px; text-align: left; border: none; background: transparent; outline: none; padding: 0;
  font: 700 12.5px ui-monospace, monospace; color: #f6d27a; caret-color: #f0b429;
}
.cne-test-field::placeholder { color: #93803f; font-weight: 600; }
`;
