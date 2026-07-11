/**
 * WorkflowDefinitions.jsx
 * ---------------------------------------------------------------------------
 * Drop-in React component for the Knotarium "Workflow Definitions" panel.
 * Adds: inline rename, collapsible groups (create / rename / delete),
 * per-card group reassignment, drag & drop (reorder + move between groups),
 * and a Comfortable/Compact density toggle.
 *
 * No external deps. Styles are injected once into <head> under prefixed
 * class names (kwf-*), so nothing collides with your app's CSS.
 *
 * ============================  HOW TO USE  =================================
 * 1. Drop this file in (e.g. src/components/WorkflowDefinitions.jsx).
 *    Using TypeScript? Rename to .tsx and add the prop types noted inline.
 *
 * 2. It is a CONTROLLED component. YOU own the data; the component never
 *    mutates it — it calls back on every change so you can persist.
 *
 *      <WorkflowDefinitions
 *        workflows={workflows}   // ORDERED array (render order = this order)
 *        groups={groups}
 *        onRenameWorkflow={(id, name) => ...}
 *        onMoveWorkflow={(id, { group, beforeId }) => ...}  // reorder + regroup
 *        onCreateGroup={(name) => ...}     // return the new group's id
 *        onRenameGroup={(id, name) => ...}
 *        onDeleteGroup={(id) => ...}       // reassign its workflows to null
 *        onViewGraph={(id) => ...}
 *        onTriggerRun={(id) => ...}
 *      />
 *
 *    Shapes:
 *      workflow = { id, name, group: string | null, nodes: number, conns: number }
 *      group    = { id, name, color }   // color = any CSS color (the dot)
 *
 * 3. WIRE THE CALLBACKS TO YOUR BACKEND. The handlers are where your API /
 *    store updates go. Minimum you need server-side:
 *      - PATCH workflow.name              (onRenameWorkflow)
 *      - PATCH workflow.group + order     (onMoveWorkflow)
 *      - CRUD on groups                   (onCreate/Rename/DeleteGroup)
 *    `onMoveWorkflow` gives you the target group and the id to insert BEFORE
 *    (null = append to end of that group). Persist both group + position.
 *    If you don't track order yet, add an `order`/`position` column, or store
 *    an array of ids per group.
 *
 * 4. If you have no group concept yet: pass groups={[]} — everything renders
 *    under "Ungrouped" and rename/density/reorder still work. Add grouping later.
 *
 * 5. The fastest way to see it working: import { WorkflowDefinitionsDemo } at
 *    the bottom of this file — it wires local useState so you can click around,
 *    then copy that wiring into your real data layer.
 *
 * ----------------------------  WHAT TO CHANGE  -----------------------------
 *  - Replace the demo state in WorkflowDefinitionsDemo with your data source.
 *  - Point the 8 callbacks at your API/store.
 *  - (Optional) tune colors at the top of CSS (kwf- vars) to match exactly.
 *  - (Optional) default density: change DEFAULT_DENSITY below.
 *  - Header/title strip is intentionally NOT included — mount this inside your
 *    existing "Workflow Definitions" section. The panel renders just the list +
 *    its toolbar (density toggle + New Group).
 * ===========================================================================
 */

import React, { useState, useRef, useEffect } from "react";

const DEFAULT_DENSITY = "compact"; // "compact" | "comfortable"
const GROUP_COLORS = ["#34d399", "#a78bfa", "#22d3ee", "#f0b429", "#f0556d", "#60a5fa"];

/* ----------------------------- icons ----------------------------- */
const I = {
  plus: <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round"><path d="M12 5v14M5 12h14" /></svg>,
  chev: <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round"><path d="M6 9l6 6 6-6" /></svg>,
  pencil: <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 20h9M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4z" /></svg>,
  grip: <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor"><circle cx="9" cy="6" r="1.6" /><circle cx="15" cy="6" r="1.6" /><circle cx="9" cy="12" r="1.6" /><circle cx="15" cy="12" r="1.6" /><circle cx="9" cy="18" r="1.6" /><circle cx="15" cy="18" r="1.6" /></svg>,
  eye: <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z" /><circle cx="12" cy="12" r="3" /></svg>,
  play: <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><path d="M7 5l12 7-12 7z" /></svg>,
  check: <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round"><path d="M5 13l4 4L19 7" /></svg>,
  trash: <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M3 6h18M8 6V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v2m2 0v14a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1V6" /></svg>,
};

/* ----------------------------- styles ----------------------------- */
const CSS = `
.kwf-root { color: #e6edf3; font-family: inherit; }
.kwf-toolbar { display: flex; align-items: center; gap: 10px; margin-bottom: 16px; }
.kwf-density { display: flex; flex: none; background: #0e1623; border: 1px solid #1f2a3a; border-radius: 9px; padding: 3px; gap: 2px; }
.kwf-density button { border: 0; background: transparent; color: #6b7686; padding: 6px 13px; border-radius: 7px; font-size: 12px; font-weight: 600; cursor: pointer; }
.kwf-density button.kwf-active { background: #1c2840; color: #e6edf3; }
.kwf-newgroup { margin-left: auto; flex: none; white-space: nowrap; display: inline-flex; align-items: center; gap: 6px; background: #121a28; border: 1px solid #1f2a3a; color: #aeb9c8; border-radius: 9px; padding: 7px 12px; font-size: 12.5px; font-weight: 600; cursor: pointer; }
.kwf-newgroup:hover { border-color: #2f3d52; color: #e6edf3; }

.kwf-group { margin-bottom: 14px; }
.kwf-gbar { display: flex; align-items: center; gap: 10px; padding: 9px 12px; border-radius: 11px; cursor: pointer; user-select: none; border: 1px solid transparent; }
.kwf-gbar:hover { background: #0d131e; border-color: #16202e; }
.kwf-gbar.kwf-dragover { background: #111733; border-color: #6f6cf0; }
.kwf-gchev { color: #5a6675; transition: transform .18s ease; display: inline-flex; }
.kwf-gchev.kwf-col { transform: rotate(-90deg); }
.kwf-gdot { width: 9px; height: 9px; border-radius: 50%; flex: none; }
.kwf-gname { font-size: 13.5px; font-weight: 700; }
.kwf-gname-input { font-size: 13.5px; font-weight: 700; background: #0a0f18; border: 1px solid #2f6df0; border-radius: 6px; color: #fff; padding: 3px 8px; outline: none; }
.kwf-gcount { font-size: 11px; color: #5a6675; font-weight: 600; background: #121a28; padding: 2px 8px; border-radius: 999px; }
.kwf-gactions { margin-left: auto; display: flex; gap: 4px; opacity: 0; transition: opacity .15s; }
.kwf-gbar:hover .kwf-gactions { opacity: 1; }
.kwf-gact { width: 26px; height: 26px; display: grid; place-items: center; border-radius: 7px; background: transparent; border: 0; color: #6b7686; cursor: pointer; }
.kwf-gact:hover { background: #16202e; color: #cdd6e2; }
.kwf-gbody { padding: 8px 0 4px 14px; display: flex; flex-direction: column; gap: 12px; border-left: 1px dashed #1a2433; margin-left: 16px; }
.kwf-gbody.kwf-append { background: rgba(111,108,240,.05); border-left-color: #6f6cf0; border-radius: 0 0 12px 12px; }

.kwf-card { position: relative; background: #0d1422; border: 1px solid #18222f; border-radius: 16px; padding: 18px 20px; transition: border-color .15s, opacity .15s; }
.kwf-card:hover { border-color: #24324a; }
.kwf-card.kwf-dragging { opacity: .35; border-style: dashed; border-color: #6f6cf0; }
.kwf-card.kwf-drop::before { content: ""; position: absolute; left: 4px; right: 4px; top: -7px; height: 3px; border-radius: 3px; background: #6f6cf0; box-shadow: 0 0 10px rgba(111,108,240,.7); }
.kwf-ctop { display: flex; align-items: flex-start; gap: 12px; }
.kwf-grip { color: #3a4759; cursor: grab; padding-top: 3px; display: inline-flex; }
.kwf-card:hover .kwf-grip { color: #58697f; }
.kwf-namearea { flex: 1; min-width: 0; }
.kwf-namerow { display: flex; align-items: center; gap: 9px; }
.kwf-name { font-size: 18px; font-weight: 700; color: #f4f7fb; }
.kwf-editbtn { width: 28px; height: 28px; display: grid; place-items: center; border-radius: 8px; background: transparent; border: 1px solid transparent; color: #4d5b6e; cursor: pointer; opacity: 0; transition: opacity .15s, color .15s, border-color .15s; }
.kwf-namerow:hover .kwf-editbtn { opacity: 1; }
.kwf-editbtn:hover { color: #8fd3ff; border-color: rgba(59,158,255,.4); background: rgba(59,158,255,.1); }
.kwf-name-input { font-size: 18px; font-weight: 700; background: #0a0f18; border: 1px solid #3b82f6; border-radius: 8px; color: #fff; padding: 4px 10px; outline: none; min-width: 220px; box-shadow: 0 0 0 3px rgba(59,130,246,.15); }
.kwf-editactions { display: flex; gap: 6px; }
.kwf-mini { display: inline-flex; align-items: center; gap: 5px; border-radius: 8px; padding: 6px 11px; font-size: 12px; font-weight: 600; cursor: pointer; border: 1px solid; }
.kwf-mini-save { background: #1a7f52; border-color: #25a06a; color: #eafff4; }
.kwf-mini-cancel { background: transparent; border-color: #2a3647; color: #8995a6; }
.kwf-id { font-size: 12px; color: #4f5b6b; font-family: ui-monospace, Menlo, monospace; margin-top: 5px; }
.kwf-pills { display: flex; gap: 8px; flex: none; }
.kwf-pill { font-size: 12px; color: #9aa6b5; background: #131c2a; border: 1px solid #1e2a3a; border-radius: 8px; padding: 5px 11px; font-weight: 500; }
.kwf-cfoot { display: flex; align-items: center; gap: 12px; margin-top: 16px; padding-top: 15px; border-top: 1px solid #141d29; }
.kwf-actions { margin-left: auto; display: flex; gap: 10px; }
.kwf-act { display: inline-flex; align-items: center; gap: 7px; border-radius: 10px; padding: 9px 15px; font-size: 13px; font-weight: 600; cursor: pointer; border: 1px solid; white-space: nowrap; }
.kwf-act-ghost { background: #111927; border-color: #202c3d; color: #b6c2d2; }
.kwf-act-ghost:hover { border-color: #2f3d52; }
.kwf-act-run { background: rgba(28,142,90,.16); border-color: rgba(45,180,120,.4); color: #57e6a0; }
.kwf-act-run:hover { background: rgba(28,142,90,.28); }

/* compact density */
.kwf-card.kwf-compact { display: flex; align-items: center; gap: 12px; padding: 11px 9px 11px 14px; }
.kwf-card.kwf-compact .kwf-name { font-size: 15px; white-space: nowrap; }
.kwf-card.kwf-compact .kwf-namearea { flex: 1; min-width: 0; }
.kwf-card.kwf-compact .kwf-name-input { font-size: 15px; min-width: 180px; }
.kwf-cmeta { font-size: 12px; color: #5f6c7d; font-family: ui-monospace, Menlo, monospace; white-space: nowrap; flex: none; }
.kwf-cactions { display: flex; gap: 4px; flex: none; opacity: 0; transition: opacity .15s; }
.kwf-card.kwf-compact:hover .kwf-cactions { opacity: 1; }
.kwf-iconbtn { width: 32px; height: 32px; display: grid; place-items: center; border-radius: 9px; border: 1px solid transparent; background: transparent; cursor: pointer; }
.kwf-iconbtn.kwf-ghost { color: #8995a6; }
.kwf-iconbtn.kwf-ghost:hover { background: #141d29; border-color: #243245; color: #cdd6e2; }
.kwf-iconbtn.kwf-run { color: #57e6a0; }
.kwf-iconbtn.kwf-run:hover { background: rgba(28,142,90,.2); border-color: rgba(45,180,120,.4); }

/* group chip */
.kwf-chipwrap { position: relative; }
.kwf-chip { display: inline-flex; align-items: center; gap: 8px; background: #101826; border: 1px solid #1f2a3a; border-radius: 9px; padding: 7px 11px; font-size: 12.5px; font-weight: 600; color: #c5cede; cursor: pointer; }
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
`;

let stylesInjected = false;
function useInjectStyles() {
  useEffect(() => {
    if (stylesInjected) return;
    const el = document.createElement("style");
    el.setAttribute("data-knot-wf", "");
    el.textContent = CSS;
    document.head.appendChild(el);
    stylesInjected = true;
  }, []);
}

/* ----------------------------- pieces ----------------------------- */
function NameEditor({ value, fontClass, onSave, onCancel }) {
  const ref = useRef(null);
  const [val, setVal] = useState(value);
  useEffect(() => { if (ref.current) { ref.current.focus(); ref.current.select(); } }, []);
  return (
    <div className="kwf-namerow">
      <input
        ref={ref}
        className={fontClass}
        value={val}
        onChange={(e) => setVal(e.target.value)}
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => {
          if (e.key === "Enter") onSave(val.trim() || value);
          if (e.key === "Escape") onCancel();
        }}
      />
      <div className="kwf-editactions">
        <button className="kwf-mini kwf-mini-save" onClick={() => onSave(val.trim() || value)}>{I.check} Save</button>
        <button className="kwf-mini kwf-mini-cancel" onClick={onCancel}>Cancel</button>
      </div>
    </div>
  );
}

function GroupChip({ wf, groups, onAssign, mini }) {
  const [open, setOpen] = useState(false);
  const ref = useRef(null);
  useEffect(() => {
    const h = (e) => { if (ref.current && !ref.current.contains(e.target)) setOpen(false); };
    document.addEventListener("mousedown", h);
    return () => document.removeEventListener("mousedown", h);
  }, []);
  const cur = groups.find((g) => g.id === wf.group);
  return (
    <div className="kwf-chipwrap" ref={ref}>
      <button className={"kwf-chip" + (mini ? " kwf-mini" : "")} onClick={(e) => { e.stopPropagation(); setOpen((o) => !o); }}>
        <span className="kwf-cdot" style={{ background: cur ? cur.color : "#3a4759" }} />
        {cur ? cur.name : "Ungrouped"}
        <span className="kwf-cchev">{I.chev}</span>
      </button>
      {open && (
        <div className="kwf-menu" onClick={(e) => e.stopPropagation()}>
          <div className="kwf-mlabel">MOVE TO GROUP</div>
          {groups.map((g) => (
            <div key={g.id} className="kwf-mitem" onClick={() => { onAssign(wf.id, g.id); setOpen(false); }}>
              <span className="kwf-mdot" style={{ background: g.color }} />
              {g.name}
              {wf.group === g.id && <span className="kwf-mcheck">{I.check}</span>}
            </div>
          ))}
          <div className="kwf-msep" />
          <div className="kwf-mitem" onClick={() => { onAssign(wf.id, null); setOpen(false); }}>
            <span className="kwf-mdot" style={{ background: "#3a4759" }} />
            Ungrouped
            {!wf.group && <span className="kwf-mcheck">{I.check}</span>}
          </div>
        </div>
      )}
    </div>
  );
}

function Card({ wf, groups, compact, dragging, dropTarget, onRename, onAssign, onViewGraph, onTriggerRun, onDragStart, onDragEnd, onDragOverCard, onDropCard }) {
  const [editing, setEditing] = useState(false);
  const dragProps = {
    draggable: !editing,
    onDragStart: (e) => { e.stopPropagation(); onDragStart(wf.id); },
    onDragEnd,
    onDragOver: (e) => { e.preventDefault(); e.stopPropagation(); onDragOverCard(wf.id); },
    onDrop: (e) => { e.preventDefault(); e.stopPropagation(); onDropCard(wf.id); },
  };
  const cls = (base) => base + (dragging ? " kwf-dragging" : "") + (dropTarget ? " kwf-drop" : "");
  const nameBlock = editing ? (
    <NameEditor value={wf.name} fontClass="kwf-name-input" onSave={(v) => { onRename(wf.id, v); setEditing(false); }} onCancel={() => setEditing(false)} />
  ) : (
    <div className="kwf-namerow">
      <span className="kwf-name">{wf.name}</span>
      <button className="kwf-editbtn" title="Rename" onClick={(e) => { e.stopPropagation(); setEditing(true); }}>{I.pencil}</button>
    </div>
  );

  if (compact) {
    return (
      <div className={cls("kwf-card kwf-compact")} title={"ID: " + wf.id} {...dragProps}>
        <span className="kwf-grip" title="Drag to reorder or move between groups">{I.grip}</span>
        <div className="kwf-namearea">{nameBlock}</div>
        <span className="kwf-cmeta">{wf.nodes} nodes · {wf.conns} conn</span>
        <GroupChip wf={wf} groups={groups} onAssign={onAssign} mini />
        <div className="kwf-cactions">
          <button className="kwf-iconbtn kwf-ghost" title="View Graph" onClick={() => onViewGraph(wf.id)}>{I.eye}</button>
          <button className="kwf-iconbtn kwf-run" title="Trigger Run" onClick={() => onTriggerRun(wf.id)}>{I.play}</button>
        </div>
      </div>
    );
  }

  return (
    <div className={cls("kwf-card")} {...dragProps}>
      <div className="kwf-ctop">
        <span className="kwf-grip" title="Drag to reorder or move between groups">{I.grip}</span>
        <div className="kwf-namearea">
          {nameBlock}
          <div className="kwf-id">ID: {wf.id}</div>
        </div>
        <div className="kwf-pills">
          <span className="kwf-pill">{wf.nodes} Nodes</span>
          <span className="kwf-pill">{wf.conns} Connections</span>
        </div>
      </div>
      <div className="kwf-cfoot">
        <GroupChip wf={wf} groups={groups} onAssign={onAssign} />
        <div className="kwf-actions">
          <button className="kwf-act kwf-act-ghost" onClick={() => onViewGraph(wf.id)}>{I.eye} View Graph</button>
          <button className="kwf-act kwf-act-run" onClick={() => onTriggerRun(wf.id)}>{I.play} Trigger Run</button>
        </div>
      </div>
    </div>
  );
}

/* ----------------------------- main ----------------------------- */
export default function WorkflowDefinitions({
  workflows = [],
  groups = [],
  onRenameWorkflow = () => {},
  onMoveWorkflow = () => {},
  onCreateGroup = () => {},
  onRenameGroup = () => {},
  onDeleteGroup = () => {},
  onViewGraph = () => {},
  onTriggerRun = () => {},
}) {
  useInjectStyles();
  const [density, setDensity] = useState(DEFAULT_DENSITY);
  const [collapsed, setCollapsed] = useState({});
  const [editingGroup, setEditingGroup] = useState(undefined);
  const [dragId, setDragId] = useState(null);
  const [dragOverGroup, setDragOverGroup] = useState(null);
  const [dragOverId, setDragOverId] = useState(null);

  const clearDrag = () => { setDragId(null); setDragOverGroup(null); setDragOverId(null); };
  const dropOnGroup = (group) => { if (dragId) onMoveWorkflow(dragId, { group, beforeId: null }); clearDrag(); };
  const dropOnCard = (targetId) => {
    if (!dragId || dragId === targetId) { clearDrag(); return; }
    const target = workflows.find((x) => x.id === targetId);
    if (target) onMoveWorkflow(dragId, { group: target.group, beforeId: targetId });
    clearDrag();
  };

  const handleCreateGroup = () => {
    const color = GROUP_COLORS[groups.length % GROUP_COLORS.length];
    const newId = onCreateGroup("New Group", color);
    if (newId) setEditingGroup(newId); // focus rename if parent returns the id
  };

  const sections = [...groups, { id: null, name: "Ungrouped", color: "#3a4759", ungrouped: true }];

  return (
    <div className="kwf-root">
      <div className="kwf-toolbar">
        <div className="kwf-density">
          <button className={density === "comfortable" ? "kwf-active" : ""} onClick={() => setDensity("comfortable")}>Comfortable</button>
          <button className={density === "compact" ? "kwf-active" : ""} onClick={() => setDensity("compact")}>Compact</button>
        </div>
        <button className="kwf-newgroup" onClick={handleCreateGroup}>{I.plus} New Group</button>
      </div>

      {sections.map((g) => {
        const items = workflows.filter((x) => x.group === g.id);
        if (g.ungrouped && items.length === 0) return null;
        const isCol = collapsed[g.id];
        const overThis = dragOverGroup === g.id;
        return (
          <div className="kwf-group" key={g.id || "__ungrouped"}>
            <div
              className={"kwf-gbar" + (overThis && dragId ? " kwf-dragover" : "")}
              onClick={() => setCollapsed((c) => ({ ...c, [g.id]: !c[g.id] }))}
              onDragOver={(e) => { e.preventDefault(); setDragOverGroup(g.id); setDragOverId(null); }}
              onDragLeave={() => setDragOverGroup((d) => (d === g.id ? null : d))}
              onDrop={() => dropOnGroup(g.id)}
            >
              <span className={"kwf-gchev" + (isCol ? " kwf-col" : "")}>{I.chev}</span>
              <span className="kwf-gdot" style={{ background: g.color }} />
              {!g.ungrouped && editingGroup === g.id ? (
                <input
                  className="kwf-gname-input"
                  autoFocus
                  defaultValue={g.name}
                  onClick={(e) => e.stopPropagation()}
                  onBlur={(e) => { onRenameGroup(g.id, e.target.value.trim() || g.name); setEditingGroup(undefined); }}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") { onRenameGroup(g.id, e.target.value.trim() || g.name); setEditingGroup(undefined); }
                    if (e.key === "Escape") setEditingGroup(undefined);
                  }}
                />
              ) : (
                <span className="kwf-gname">{g.name}</span>
              )}
              <span className="kwf-gcount">{items.length}</span>
              {!g.ungrouped && (
                <div className="kwf-gactions" onClick={(e) => e.stopPropagation()}>
                  <button className="kwf-gact" title="Rename group" onClick={() => setEditingGroup(g.id)}>{I.pencil}</button>
                  <button className="kwf-gact" title="Delete group" onClick={() => onDeleteGroup(g.id)}>{I.trash}</button>
                </div>
              )}
            </div>

            {!isCol && items.length > 0 && (
              <div
                className={"kwf-gbody" + (overThis && dragOverId === null && dragId ? " kwf-append" : "")}
                onDragOver={(e) => { e.preventDefault(); setDragOverGroup(g.id); setDragOverId(null); }}
                onDrop={(e) => { e.preventDefault(); dropOnGroup(g.id); }}
              >
                {items.map((x) => (
                  <Card
                    key={x.id}
                    wf={x}
                    groups={groups}
                    compact={density === "compact"}
                    dragging={dragId === x.id}
                    dropTarget={dragOverId === x.id && dragId !== null && dragId !== x.id}
                    onRename={onRenameWorkflow}
                    onAssign={onMoveWorkflowAssign(onMoveWorkflow)}
                    onViewGraph={onViewGraph}
                    onTriggerRun={onTriggerRun}
                    onDragStart={(id) => setDragId(id)}
                    onDragEnd={clearDrag}
                    onDragOverCard={(id) => { setDragOverId(id); setDragOverGroup(g.id); }}
                    onDropCard={dropOnCard}
                  />
                ))}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}

// The group chip reassigns group only (append to end of target group).
function onMoveWorkflowAssign(onMoveWorkflow) {
  return (id, group) => onMoveWorkflow(id, { group, beforeId: null });
}

/* ===========================================================================
 * OPTIONAL: copy this demo wiring into your app, then swap the local state
 * for your real data source + API calls. Delete before shipping.
 * ===========================================================================
 *
 * export function WorkflowDefinitionsDemo() {
 *   const [groups, setGroups] = React.useState([
 *     { id: "prod", name: "Production", color: "#34d399" },
 *     { id: "exp",  name: "Experiments", color: "#a78bfa" },
 *   ]);
 *   const [wf, setWf] = React.useState([
 *     { id: "29a702f7", name: "Order Sync",        group: "prod", nodes: 3, conns: 2 },
 *     { id: "ff705b60", name: "Knotarium Flow",  group: null,   nodes: 3, conns: 2 },
 *     { id: "b21c98a4", name: "Daily Digest Email", group: "exp", nodes: 5, conns: 4 },
 *   ]);
 *
 *   const move = (id, { group, beforeId }) => setWf((list) => {
 *     const moving = list.find((x) => x.id === id);
 *     if (!moving) return list;
 *     const rest = list.filter((x) => x.id !== id);
 *     const updated = { ...moving, group };
 *     if (beforeId) {
 *       const idx = rest.findIndex((x) => x.id === beforeId);
 *       rest.splice(idx < 0 ? rest.length : idx, 0, updated);
 *     } else {
 *       let last = -1; rest.forEach((x, i) => { if (x.group === group) last = i; });
 *       rest.splice(last + 1, 0, updated);
 *     }
 *     return rest; // <-- persist this order to your backend here
 *   });
 *
 *   return (
 *     <WorkflowDefinitions
 *       workflows={wf}
 *       groups={groups}
 *       onRenameWorkflow={(id, name) => setWf((l) => l.map((x) => x.id === id ? { ...x, name } : x))}
 *       onMoveWorkflow={move}
 *       onCreateGroup={(name, color) => {
 *         const id = "g" + Date.now();
 *         setGroups((g) => [...g, { id, name, color }]);
 *         return id; // returning the id lets the panel focus the rename input
 *       }}
 *       onRenameGroup={(id, name) => setGroups((g) => g.map((x) => x.id === id ? { ...x, name } : x))}
 *       onDeleteGroup={(id) => {
 *         setWf((l) => l.map((x) => x.group === id ? { ...x, group: null } : x));
 *         setGroups((g) => g.filter((x) => x.id !== id));
 *       }}
 *       onViewGraph={(id) => console.log("view", id)}
 *       onTriggerRun={(id) => console.log("run", id)}
 *     />
 *   );
 * }
 */
