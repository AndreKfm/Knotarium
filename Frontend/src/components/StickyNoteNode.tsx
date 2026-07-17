// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useReactFlow, useStore, NodeResizer } from '@xyflow/react';
import type { NodeProps } from '@xyflow/react';
import {
  getStickyNoteColor,
  getStickyNoteColorId,
  applyStickyNoteText,
  applyStickyNoteColor,
  STICKY_NOTE_COLORS,
} from '../node-editor/stickyNote';

/**
 * Editor-only sticky-note annotation (#13). Renders an editable, resizable,
 * colourable card with no connection ports. Text/colour persist in
 * `data.properties`; size in `node.style` (NodeResizer writes it directly), both
 * round-tripped by schemaMapper's `_metadata` channel.
 *
 * In the read-only run view the note shows its text but can't be edited or
 * recoloured (the run canvas sets `nodesConnectable=false`, our read-only signal).
 */
export function StickyNoteNode({ id, data, selected }: NodeProps) {
  const { setNodes } = useReactFlow();
  const isReadOnly = useStore((s) => !s.nodesConnectable);

  const props = (data?.properties as Record<string, unknown> | undefined) || {};
  const text = typeof props.text === 'string' ? props.text : '';
  const colorId = getStickyNoteColorId({ data });
  const color = getStickyNoteColor(colorId);

  const setText = (value: string) => setNodes((nds) => applyStickyNoteText(nds, id, value));
  const setColor = (nextColorId: string) => setNodes((nds) => applyStickyNoteColor(nds, id, nextColorId));

  return (
    <div
      className={`sticky-note${selected ? ' sticky-note-selected' : ''}`}
      style={{
        width: '100%',
        height: '100%',
        boxSizing: 'border-box',
        background: color.bg,
        border: `1px solid ${color.border}`,
        borderRadius: '14px',
        // A coloured wash, not a solid card: a soft halo in the note's colour
        // that wraps the whole card evenly (centred, not pooled below) plus a
        // faint top highlight, with the canvas blurred behind.
        boxShadow: selected
          ? `0 0 0 3px ${color.border}, 0 0 26px -10px ${color.glow}, inset 0 1px 0 rgba(255,255,255,.10)`
          : `0 0 26px -10px ${color.glow}, inset 0 1px 0 rgba(255,255,255,.10)`,
        backdropFilter: 'blur(3px)',
        WebkitBackdropFilter: 'blur(3px)',
        display: 'flex',
        flexDirection: 'column',
        padding: '8px',
        gap: '6px',
        overflow: 'hidden',
      }}
    >
      {!isReadOnly && (
        <NodeResizer
          minWidth={140}
          minHeight={90}
          isVisible={Boolean(selected)}
          lineStyle={{ borderColor: color.border }}
          handleStyle={{ backgroundColor: color.border, border: 'none', borderRadius: '3px' }}
        />
      )}

      {isReadOnly ? (
        <div
          style={{
            flex: 1,
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
            color: color.text,
            fontSize: '0.8rem',
            lineHeight: 1.4,
            overflow: 'auto',
          }}
        >
          {text}
        </div>
      ) : (
        <textarea
          className="nodrag"
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder="Write a note…"
          aria-label="Sticky note text"
          style={{
            flex: 1,
            resize: 'none',
            background: 'transparent',
            border: 'none',
            outline: 'none',
            color: color.text,
            fontSize: '0.8rem',
            lineHeight: 1.4,
            fontFamily: 'inherit',
            width: '100%',
          }}
        />
      )}

      {/* Colour swatches — only while selected and editable. */}
      {selected && !isReadOnly && (
        <div className="nodrag" style={{ display: 'flex', gap: '5px', alignItems: 'center' }}>
          {STICKY_NOTE_COLORS.map((c) => (
            <button
              key={c.id}
              type="button"
              aria-label={`${c.label} note`}
              aria-pressed={c.id === colorId}
              title={c.label}
              onClick={(e) => { e.stopPropagation(); setColor(c.id); }}
              style={{
                width: '14px',
                height: '14px',
                borderRadius: '50%',
                background: c.swatch,
                border: c.id === colorId ? '2px solid #fff' : '1px solid rgba(255,255,255,0.3)',
                cursor: 'pointer',
                padding: 0,
              }}
            />
          ))}
        </div>
      )}
    </div>
  );
}
