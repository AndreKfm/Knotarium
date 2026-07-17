// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect } from 'react';

const CSS = `
.kwf-root { color: #e6edf3; font-family: inherit; }
.kwf-toolbar { display: flex; align-items: center; gap: 10px; margin-bottom: 24px; }
.kwf-density { display: flex; flex: none; background: #0e1623; border: 1px solid #1f2a3a; border-radius: 9px; padding: 3px; gap: 2px; }
.kwf-density button { border: 0; background: transparent; color: #6b7686; padding: 6px 13px; border-radius: 7px; font-size: 12px; font-weight: 600; cursor: pointer; }
.kwf-density button.kwf-active { background: #1c2840; color: #e6edf3; }
.kwf-newgroup { margin-left: auto; flex: none; white-space: nowrap; display: inline-flex; align-items: center; gap: 6px; background: #121a28; border: 1px solid #1f2a3a; color: #aeb9c8; border-radius: 9px; padding: 7px 12px; font-size: 12.5px; font-weight: 600; cursor: pointer; }
.kwf-newgroup:hover { border-color: #2f3d52; color: #e6edf3; }

.kwf-group { margin-bottom: 14px; }
.kwf-gbar { display: flex; align-items: center; gap: 10px; padding: 9px 12px; border-radius: 9px; cursor: pointer; user-select: none; border: 1px solid transparent; position: relative; background: #0a0e16; }
.kwf-gbar:hover { background: #0d131e; border-color: #16202e; }
.kwf-gbar.kwf-dragover { background: #111733; border-color: #6f6cf0; }
.kwf-gchev { color: #5a6675; transition: transform .18s ease; display: inline-flex; }
.kwf-gchev.kwf-col { transform: rotate(-90deg); }
.kwf-gdot-container { width: 18px; height: 18px; display: grid; place-items: center; border-radius: 50%; hover: background: rgba(255,255,255,0.05); position: relative; margin-right: -2px; }
.kwf-gdot { width: 9px; height: 9px; border-radius: 50%; flex: none; cursor: pointer; transition: transform 0.12s; border: 1px solid transparent; }
.kwf-gdot:hover { transform: scale(1.3); border-color: rgba(255,255,255,0.4); }
.kwf-gname { font-size: 13.5px; font-weight: 700; }
.kwf-gname-input { font-size: 13.5px; font-weight: 700; background: #0a0f18; border: 1px solid #2f6df0; border-radius: 6px; color: #fff; padding: 3px 8px; outline: none; }
.kwf-gcount { font-size: 11px; color: #5a6675; font-weight: 600; background: #121a28; padding: 2px 8px; border-radius: 999px; }
.kwf-gactions { margin-left: auto; display: flex; gap: 4px; opacity: 0; transition: opacity .15s; }
.kwf-gbar:hover .kwf-gactions { opacity: 1; }
.kwf-gact { width: 26px; height: 26px; display: grid; place-items: center; border-radius: 7px; background: transparent; border: 0; color: #6b7686; cursor: pointer; }
.kwf-gact:hover { background: #16202e; color: #cdd6e2; }
.kwf-gbody { padding: 8px 0 4px 14px; display: flex; flex-direction: column; gap: 7px; border-left: 1px dashed #1a2433; margin-left: 16px; }
.kwf-gbody.kwf-append { background: rgba(111,108,240,.05); border-left-color: #6f6cf0; border-radius: 0 0 12px 12px; }

/* empty named groups fold into one quiet, expandable shelf line */
.kwf-emptyshelf { margin-bottom: 14px; }
.kwf-emptybar { display: flex; align-items: center; gap: 10px; padding: 9px 12px; border-radius: 9px; cursor: pointer; user-select: none; color: #6b7888; font-size: 12.5px; font-weight: 600; border: 1px solid transparent; transition: background .12s, color .12s, border-color .12s; }
.kwf-emptybar:hover { background: #0d131e; border-color: #16202e; color: #9fb0c2; }
.kwf-emptydots { display: inline-flex; gap: 4px; }
.kwf-emptydots i { width: 8px; height: 8px; border-radius: 50%; display: inline-block; }
.kwf-emptycaret { display: inline-flex; margin-left: auto; color: #5a6675; transition: transform .18s ease; }
.kwf-emptyshelf.kwf-open .kwf-emptycaret { transform: rotate(180deg); }
.kwf-emptybody { padding: 10px 0 2px 14px; margin-left: 16px; border-left: 1px dashed #1a2433; display: flex; flex-direction: column; gap: 8px; }
.kwf-emptybody .kwf-group { margin-bottom: 0; }

.kwf-card { position: relative; background: #0d1422; border: 1px solid #18222f; border-radius: 16px; padding: 18px 20px; transition: border-color .15s, opacity .15s; }
.kwf-card:hover { border-color: #24324a; }
.kwf-card.kwf-dragging { opacity: .35; border-style: dashed; border-color: #6f6cf0; }
.kwf-card.kwf-drop::before { content: ""; position: absolute; left: 4px; right: 4px; top: -7px; height: 3px; border-radius: 3px; background: #6f6cf0; box-shadow: 0 0 10px rgba(111,108,240,.7); }
.kwf-ctop { display: flex; align-items: flex-start; gap: 12px; }
.kwf-grip { color: #3a4759; cursor: grab; padding-top: 3px; display: inline-flex; }
.kwf-card:hover .kwf-grip { color: #58697f; }
.kwf-namearea { flex: 1; min-width: 0; }
.kwf-namerow { display: flex; align-items: center; gap: 9px; }
.kwf-name { font-size: 17px; font-weight: 700; color: #f4f7fb; }
.kwf-editbtn { width: 28px; height: 28px; display: grid; place-items: center; border-radius: 8px; background: transparent; border: 1px solid transparent; color: #4d5b6e; cursor: pointer; opacity: 0; transition: opacity .15s, color .15s, border-color .15s; }
.kwf-namerow:hover .kwf-editbtn { opacity: 1; }
.kwf-editbtn:hover { color: #8fd3ff; border-color: rgba(59,158,255,.4); background: rgba(59,158,255,.1); }
.kwf-name-input { font-size: 17px; font-weight: 700; background: #0a0f18; border: 1px solid #3b82f6; border-radius: 8px; color: #fff; padding: 4px 10px; outline: none; min-width: 220px; box-shadow: 0 0 0 3px rgba(59,130,246,.15); }
.kwf-editactions { display: flex; gap: 6px; }
.kwf-mini { display: inline-flex; align-items: center; gap: 5px; border-radius: 8px; padding: 6px 11px; font-size: 12px; font-weight: 600; cursor: pointer; border: 1px solid; }
.kwf-mini-save { background: #1a7f52; border-color: #25a06a; color: #eafff4; }
.kwf-mini-cancel { background: transparent; border-color: #2a3647; color: #8995a6; }
.kwf-id { font-size: 12px; color: #4f5b6b; font-family: ui-monospace, Menlo, monospace; margin-top: 5px; }
.kwf-pills { display: flex; gap: 8px; flex: none; }
.kwf-pill { font-size: 12px; color: #9aa6b5; background: #131c2a; border: 1px solid #1e2a3a; border-radius: 8px; padding: 5px 11px; font-weight: 500; }
.kwf-cfoot { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; margin-top: 16px; padding-top: 15px; border-top: 1px solid #141d29; }
.kwf-actions { margin-left: auto; display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 10px; }
.kwf-act { display: inline-flex; align-items: center; gap: 7px; border-radius: 10px; padding: 9px 15px; font-size: 13px; font-weight: 600; cursor: pointer; border: 1px solid; white-space: nowrap; }
.kwf-act-ghost { background: #111927; border-color: #202c3d; color: #b6c2d2; }
.kwf-act-ghost:hover { border-color: #2f3d52; }
.kwf-act-run { background: rgba(28,142,90,.16); border-color: rgba(45,180,120,.4); color: #57e6a0; }
.kwf-act-run:hover:not(:disabled) { background: rgba(28,142,90,.28); }
.kwf-act-run:disabled { background: rgba(255, 255, 255, 0.02); border-color: #243245; color: #4d5b6e; cursor: not-allowed; }

/* compact density */
/* Compact = a flat divider row inside the panel card (not a detached bordered pill). */
.kwf-card.kwf-compact { display: flex; align-items: center; gap: 12px; padding: 7px 12px; min-height: 44px; background: transparent; border: 0; border-top: 1px solid #111824; border-radius: 0; }
.kwf-card.kwf-compact:hover { background: #0f1622; }
/* Continuous list: kill the inter-row gap when the group holds compact rows, so the dividers join up. */
.kwf-gbody:has(> .kwf-card.kwf-compact) { gap: 0; }
.kwf-card.kwf-compact .kwf-name-input { font-size: 15px; min-width: 180px; }
.kwf-cmeta { font-size: 12px; color: #5f6c7d; font-family: ui-monospace, Menlo, monospace; white-space: nowrap; flex: none; }
.kwf-cactions { display: flex; gap: 4px; flex: none; }

/* Name = a CAPPED 320px track (not 1fr) so the meta clusters right after it instead of being flung to
   the panel edge; long names truncate (ellipsis). min-width:0 on namearea + namerow lets it shrink. */
.kwf-card.kwf-compact .kwf-namearea { flex: 0 1 320px; min-width: 0; }
.kwf-namerow { min-width: 0; }
.kwf-card.kwf-compact .kwf-name { flex: 1; min-width: 0; font-size: 15px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
/* node·conn meta sits just after the name (clustered), single line, never shrinks. The leftover width
   trails off to the right; the hover .kwf-csecondary overlay is out of flow so it doesn't reserve it. */
.kwf-card.kwf-compact .kwf-cmeta { margin-left: 26px; }
/* INACTIVE badge is a real, no-shrink flex child between name and meta — never overlaps. */
.kwf-card.kwf-compact .kwf-inactive-badge { flex: none; }

/* Drag grip: revealed on hover at the far left. */
.kwf-card.kwf-compact .kwf-grip { opacity: 0; transition: opacity .15s; }
.kwf-card.kwf-compact:hover .kwf-grip,
.kwf-card.kwf-compact:focus-within .kwf-grip { opacity: 1; }

/* Secondary controls (group tag, bell, action buttons) live in a right-pinned overlay that is OUT of
   the flex flow — so at rest the name + meta own the full row width (the prior version reserved ~380px
   of invisible controls, crushing the name to 0 and causing the overlap). They fade in over the right
   edge on hover. */
.kwf-csecondary {
  position: absolute; right: 12px; top: 50%; transform: translateY(-50%);
  display: flex; align-items: center; gap: 8px;
  opacity: 0; pointer-events: none; transition: opacity .12s;
  padding-left: 40px;
  background: linear-gradient(90deg, rgba(15,22,34,0), #0f1622 34px);
}
.kwf-card.kwf-compact:hover .kwf-csecondary,
.kwf-card.kwf-compact:focus-within .kwf-csecondary { opacity: 1; pointer-events: auto; }
/* Touch devices have no hover — fold the cluster back into flow and keep everything reachable. */
@media (hover: none) {
  .kwf-card.kwf-compact .kwf-grip { opacity: 1; }
  .kwf-csecondary { position: static; transform: none; opacity: 1; pointer-events: auto; padding-left: 0; background: none; }
}
.kwf-iconbtn { width: 32px; height: 32px; display: grid; place-items: center; border-radius: 9px; border: 1px solid transparent; background: transparent; cursor: pointer; }
.kwf-iconbtn.kwf-ghost { color: #8995a6; }
.kwf-iconbtn.kwf-ghost:hover { background: #141d29; border-color: #243245; color: #cdd6e2; }
.kwf-iconbtn.kwf-run { color: #57e6a0; }
.kwf-iconbtn.kwf-run:hover:not(:disabled) { background: rgba(28,142,90,.2); border-color: rgba(45,180,120,.4); }
.kwf-iconbtn.kwf-run:disabled { color: #3a4759; cursor: not-allowed; }
.kwf-iconbtn.kwf-del:hover { background: rgba(240,85,109,.15); border-color: rgba(240,85,109,.4); color: #f0556d; }
.kwf-act-del { background: transparent; border-color: #2a3647; color: #8995a6; }
.kwf-act-del:hover { background: rgba(240,85,109,.12); border-color: rgba(240,85,109,.4); color: #f0556d; }

/* deactivated workflow card */
.kwf-card.kwf-inactive { opacity: .62; border-color: #3a2b1a; background-image: repeating-linear-gradient(135deg, rgba(240,176,41,.05) 0 8px, transparent 8px 16px); }
.kwf-card.kwf-inactive:hover { opacity: .8; }
.kwf-card.kwf-inactive .kwf-name { color: #b9a07a; }
.kwf-inactive-badge { display: inline-flex; align-items: center; padding: 3px 9px; border-radius: 999px; font-size: 11px; font-weight: 700; letter-spacing: .04em; text-transform: uppercase; color: #f0b429; background: rgba(240,176,41,.14); border: 1px solid rgba(240,176,41,.45); }

/* active / inactive power toggle */
.kwf-pwr-on { color: #57e6a0; }
.kwf-pwr-on:hover { background: rgba(28,142,90,.16); border-color: rgba(45,180,120,.4); color: #57e6a0; }
.kwf-pwr-off { color: #6b7686; }
.kwf-pwr-off:hover { background: rgba(240,176,41,.14); border-color: rgba(240,176,41,.45); color: #f0b429; }

/* group chip */
.kwf-chipwrap { position: relative; }
.kwf-chip { display: inline-flex; align-items: center; gap: 8px; background: #101826; border: 1px solid #1f2a3a; border-radius: 9px; padding: 7px 11px; font-size: 12.5px; font-weight: 600; color: #c5cede; cursor: pointer; border: 1px solid var(--border-color, #1f2a3a); }
.kwf-chip.kwf-mini { padding: 5px 9px; font-size: 12px; }
.kwf-chip:hover { border-color: #2f3d52; }
.kwf-chip .kwf-cdot { width: 8px; height: 8px; border-radius: 50%; }
.kwf-chip .kwf-cchev { color: #5a6675; }
.kwf-menu { position: absolute; bottom: calc(100% + 7px); left: 0; background: #0e1623; border: 1px solid #233042; border-radius: 12px; padding: 6px; min-width: 200px; z-index: 30; box-shadow: 0 16px 40px rgba(0,0,0,.5); }
.kwf-mlabel { font-size: 10px; letter-spacing: .1em; font-weight: 700; color: #566173; padding: 7px 10px 5px; }
.kwf-mitem { display: flex; align-items: center; gap: 9px; padding: 8px 10px; border-radius: 8px; font-size: 13px; color: #cdd6e2; cursor: pointer; }
.kwf-mitem:hover { background: #17202e; }
.kwf-mitem .kwf-mdot { width: 8px; height: 8px; border-radius: 50%; flex: none; }
.kwf-mitem .kwf-mcheck { margin-left: auto; color: #4ade9f; }
.kwf-msep { height: 1px; background: #1a2433; margin: 5px 4px; }

/* Color Swatch Picker */
.kwf-swatch-popover {
  position: absolute;
  top: calc(100% + 5px);
  left: 0;
  background: #0e1623;
  border: 1px solid #233042;
  border-radius: 10px;
  padding: 8px;
  display: flex;
  gap: 6px;
  z-index: 40;
  box-shadow: 0 10px 25px rgba(0,0,0,.5);
}
.kwf-swatch-dot {
  width: 18px;
  height: 18px;
  border-radius: 50%;
  cursor: pointer;
  border: 2px solid transparent;
  transition: transform 0.1s;
}
.kwf-swatch-dot:hover {
  transform: scale(1.2);
}
.kwf-swatch-dot.kwf-active-swatch {
  border-color: #fff;
}

/* Modal Portal */
.kwf-modal-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background: rgba(4, 7, 13, 0.85);
  backdrop-filter: blur(4px);
  display: grid;
  place-items: center;
  z-index: 1000;
}
.kwf-modal-box {
  background: #0d1422;
  border: 1px solid #1e2a3a;
  border-radius: 18px;
  padding: 24px;
  width: 90%;
  max-width: 420px;
  box-shadow: 0 20px 50px rgba(0,0,0,0.6);
  color: #e6edf3;
}
.kwf-modal-title {
  font-size: 18px;
  font-weight: 700;
  margin-bottom: 12px;
  color: #fff;
}
.kwf-modal-body {
  font-size: 14.5px;
  color: #9aa6b5;
  line-height: 1.5;
  margin-bottom: 24px;
}
.kwf-modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 12px;
}
.kwf-btn {
  padding: 10px 18px;
  border-radius: 10px;
  font-size: 13.5px;
  font-weight: 600;
  cursor: pointer;
  border: 1px solid transparent;
}
.kwf-btn-cancel {
  background: transparent;
  border-color: #243245;
  color: #8995a6;
}
.kwf-btn-cancel:hover {
  background: rgba(255,255,255,0.02);
  color: #cdd6e2;
}
.kwf-btn-danger {
  background: #f0556d;
  color: #fff;
}
.kwf-btn-danger:hover {
  background: #ff6b81;
}
`;

let stylesInjected = false;
export function useInjectStyles() {
  useEffect(() => {
    if (stylesInjected) return;
    const el = document.createElement('style');
    el.setAttribute('data-knot-wf', '');
    el.textContent = CSS;
    document.head.appendChild(el);
    stylesInjected = true;
  }, []);
}
