// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useState, useRef } from 'react';
import { useReactFlow, useStore, NodeResizer } from '@xyflow/react';
import type { NodeProps } from '@xyflow/react';
import { ChevronDown, ChevronRight, Pencil } from 'lucide-react';
import {
  getGroupLabel,
  getGroupCollapsed,
  getGroupColor,
  getGroupColorId,
  toggleGroupCollapsed,
  applyGroupLabel,
  applyGroupColor,
  offsetGroupChildren,
  GROUP_COLORS,
} from '../node-editor/nodeGroup';

/**
 * Editor-only visual group container (#14). Two states:
 *  - **Expanded** — a translucent, colour-tinted wash with a header (collapse
 *    toggle + editable label + colour swatches when selected) and four corner
 *    resize handles. Resizing re-fits the frame around the contained nodes: a
 *    top/left drag moves the origin, so children are offset back to stay
 *    anchored (they don't move or scale).
 *  - **Collapsed** — snaps to a compact title chip (chevron + name + child
 *    count) in the group's colour; the children hide and the expanded size is
 *    remembered for restore. No resize handles while collapsed.
 *
 * The colour is for categorisation (e.g. blue = ingestion) — it tints the
 * border/header and a faint body wash only, never an opaque fill, so the canvas
 * grid and child nodes keep their contrast. Label/collapsed/colour/remembered-
 * size persist in `data.properties`; membership rides on each child's
 * `parentId`; live size in `node.style`.
 */
export function GroupNode({ id, data, selected }: NodeProps) {
  const { setNodes } = useReactFlow();
  const isReadOnly = useStore((s) => !s.nodesConnectable);
  // Live count of the group's direct children — updates as nodes are added to
  // or removed from the group (read straight off the React Flow store).
  const childCount = useStore((s) => {
    const lookup = (s as { nodeLookup?: Map<string, { parentId?: string }> }).nodeLookup;
    if (!lookup) return 0;
    let count = 0;
    lookup.forEach((n) => { if (n.parentId === id) count++; });
    return count;
  });
  const collapsed = getGroupCollapsed({ data });
  const label = getGroupLabel({ data });
  const colorId = getGroupColorId({ data });
  const color = getGroupColor(colorId);

  const [isRenaming, setIsRenaming] = useState(false);
  const [draft, setDraft] = useState('');
  // Group origin captured when a resize gesture starts, so we can offset the
  // children by the inverse of any origin shift and keep them anchored.
  const resizeStart = useRef<{ x: number; y: number } | null>(null);

  const toggle = () => setNodes((nds) => toggleGroupCollapsed(nds, id));
  const beginRename = () => {
    if (isReadOnly) return;
    setDraft(label);
    setIsRenaming(true);
  };
  const commitRename = () => {
    setIsRenaming(false);
    setNodes((nds) => applyGroupLabel(nds, id, draft.trim() || label));
  };
  const setColor = (nextColorId: string) => setNodes((nds) => applyGroupColor(nds, id, nextColorId));

  const chevronButton = (
    <button
      type="button"
      className="nodrag"
      aria-label={collapsed ? 'Expand group' : 'Collapse group'}
      aria-expanded={!collapsed}
      title={collapsed ? 'Expand group' : 'Collapse group'}
      onClick={(e) => { e.stopPropagation(); toggle(); }}
      style={{ background: 'transparent', border: 'none', color: 'inherit', cursor: 'pointer', display: 'inline-flex', padding: 0, flex: 'none' }}
    >
      {collapsed ? <ChevronRight size={15} /> : <ChevronDown size={15} />}
    </button>
  );

  const renameInput = (
    <input
      className="nodrag"
      autoFocus
      value={draft}
      aria-label="Rename group"
      onChange={(e) => setDraft(e.target.value)}
      onClick={(e) => e.stopPropagation()}
      onKeyDown={(e) => {
        e.stopPropagation();
        if (e.key === 'Enter') { e.preventDefault(); commitRename(); }
        else if (e.key === 'Escape') { e.preventDefault(); setIsRenaming(false); }
      }}
      onBlur={commitRename}
      style={{ flex: 1, minWidth: 0, font: 'inherit', color: '#fff', background: 'rgba(255,255,255,0.08)', border: `1px solid ${color.borderStrong}`, borderRadius: '4px', padding: '0 4px', outline: 'none' }}
    />
  );

  const titleSpan = (
    <span
      style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', cursor: isReadOnly ? 'default' : 'text' }}
      title={isReadOnly ? label : 'Double-click to rename'}
      onDoubleClick={isReadOnly ? undefined : (e) => { e.stopPropagation(); beginRename(); }}
    >
      {label}
    </span>
  );

  const pencilButton = !isReadOnly ? (
    <button
      type="button"
      className="nodrag"
      aria-label="Rename group"
      title="Rename group"
      onClick={(e) => { e.stopPropagation(); beginRename(); }}
      style={{ background: 'transparent', border: 'none', color: 'inherit', cursor: 'pointer', display: 'inline-flex', padding: 0, flex: 'none', opacity: 0.55 }}
    >
      <Pencil size={12} />
    </button>
  ) : null;

  // Muted child-count: a small badge on the chip ("· 5"), a subtle label when
  // expanded ("5 nodes"). Answers "is this the empty group or the big one?".
  const countBadge = (variant: 'chip' | 'expanded') => (
    <span
      aria-label={`${childCount} ${childCount === 1 ? 'node' : 'nodes'}`}
      style={{ flex: 'none', opacity: 0.6, fontWeight: 500, fontSize: variant === 'chip' ? '0.7rem' : '0.68rem', fontVariantNumeric: 'tabular-nums' }}
    >
      {variant === 'chip' ? `· ${childCount}` : `${childCount} ${childCount === 1 ? 'node' : 'nodes'}`}
    </span>
  );

  // Colour swatch picker — only in the expanded header while selected/editable.
  const swatchRow = selected && !isReadOnly ? (
    <div className="nodrag" style={{ display: 'flex', gap: '4px', alignItems: 'center', flex: 'none' }}>
      {GROUP_COLORS.map((c) => (
        <button
          key={c.id}
          type="button"
          aria-label={`${c.label} group`}
          aria-pressed={c.id === colorId}
          title={c.label}
          onClick={(e) => { e.stopPropagation(); setColor(c.id); }}
          style={{
            width: '11px',
            height: '11px',
            borderRadius: '50%',
            background: c.swatch,
            border: c.id === colorId ? '2px solid #fff' : '1px solid rgba(255,255,255,0.3)',
            cursor: 'pointer',
            padding: 0,
          }}
        />
      ))}
    </div>
  ) : null;

  // Collapsed: a compact, self-contained title chip (no body, no handles).
  if (collapsed) {
    return (
      <div
        className={`node-group node-group-chip${selected ? ' node-group-selected' : ''}`}
        style={{
          width: '100%',
          height: '100%',
          boxSizing: 'border-box',
          display: 'flex',
          alignItems: 'center',
          gap: '6px',
          padding: '0 9px',
          background: color.headerChip,
          border: `1.5px solid ${selected ? 'var(--color-accent)' : color.borderStrong}`,
          borderRadius: '999px',
          boxShadow: `0 0 18px -8px ${color.glowStrong}`,
          color: color.text,
          fontWeight: 600,
          fontSize: '0.72rem',
          overflow: 'hidden',
        }}
      >
        {chevronButton}
        {isRenaming ? renameInput : (
          <>
            {titleSpan}
            {countBadge('chip')}
            {pencilButton}
          </>
        )}
      </div>
    );
  }

  // Expanded: the translucent wash body with a header strip and resize handles.
  return (
    <div
      className={`node-group${selected ? ' node-group-selected' : ''}`}
      style={{
        width: '100%',
        height: '100%',
        boxSizing: 'border-box',
        // Faint colour wash — translucent so the canvas grid and the child
        // nodes inside keep their normal contrast (handoff Change 2).
        background: color.body,
        border: `1.5px ${selected ? 'solid' : 'dashed'} ${selected ? 'var(--color-accent)' : color.borderSoft}`,
        borderRadius: '17px',
        // Soft, evenly-wrapping halo that recedes behind the nodes (centred, no
        // inset hairline so the border stays single).
        boxShadow: selected ? `0 0 34px -14px ${color.glowStrong}` : `0 0 34px -16px ${color.glowSoft}`,
        display: 'flex',
        flexDirection: 'column',
        overflow: 'hidden',
      }}
    >
      {!isReadOnly && (
        <NodeResizer
          minWidth={160}
          minHeight={90}
          isVisible={Boolean(selected)}
          lineStyle={{ borderColor: 'var(--color-accent)' }}
          // Ringed indigo dots: filled accent with a dark canvas-coloured ring
          // so they read as crisp handles against the wash (handoff geometry).
          handleStyle={{ width: 10, height: 10, backgroundColor: 'var(--color-accent)', border: '2px solid #0a0e16', borderRadius: '50%' }}
          onResizeStart={(_e, p) => { resizeStart.current = { x: p.x, y: p.y }; }}
          onResizeEnd={(_e, p) => {
            const start = resizeStart.current;
            resizeStart.current = null;
            if (!start) return;
            // If the origin moved (top/left handle), shift children back so they
            // stay put in absolute space while the frame resizes around them.
            const dx = start.x - p.x;
            const dy = start.y - p.y;
            if (dx !== 0 || dy !== 0) setNodes((nds) => offsetGroupChildren(nds, id, dx, dy));
          }}
        />
      )}

      {/* Header strip — the only interactive chrome; the body stays click-through-ish
          so children on top remain selectable. */}
      <div
        className="node-group-header"
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: '6px',
          height: '28px',
          padding: '0 8px',
          background: color.headerExpanded,
          borderBottom: `1px solid ${color.borderSoft}`,
          borderRadius: '16px 16px 0 0',
          color: color.text,
          fontWeight: 600,
          fontSize: '0.72rem',
          flex: 'none',
        }}
      >
        {chevronButton}
        {isRenaming ? renameInput : (
          <>
            {titleSpan}
            {countBadge('expanded')}
            {swatchRow}
            {pencilButton}
          </>
        )}
      </div>
    </div>
  );
}
