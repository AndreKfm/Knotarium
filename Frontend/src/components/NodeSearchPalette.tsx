// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useMemo, useRef, useState } from 'react';
import { searchNodes, type SearchableNode } from '../node-editor/nodeSearch';

export interface NodeSearchPaletteProps<N extends SearchableNode> {
  nodes: readonly N[];
  onClose: () => void;
  /** Called when the user picks a result (Enter or click). */
  onPick: (node: N) => void;
}

/**
 * Search / jump palette (Ctrl+F / Cmd+K). A small overlay that fuzzy-filters
 * canvas nodes by their title; picking one jumps the canvas to it.
 *
 * Mounted only while open (the parent gates rendering), so query + highlight
 * state start fresh each time and there's no reset-in-effect. The parent owns
 * the open flag and the jump action; this component owns input + navigation.
 */
export function NodeSearchPalette<N extends SearchableNode>({
  nodes,
  onClose,
  onPick,
}: NodeSearchPaletteProps<N>) {
  const [query, setQuery] = useState('');
  const [activeIndex, setActiveIndex] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  const results = useMemo(() => searchNodes(nodes, query), [nodes, query]);
  // Derive (rather than store) the in-range highlight so a shrinking result
  // list can't point past the end — avoids a clamp-in-effect.
  const safeIndex = results.length === 0 ? 0 : Math.min(activeIndex, results.length - 1);

  // Focus the input on mount (DOM side-effect, not state).
  useEffect(() => {
    const id = requestAnimationFrame(() => inputRef.current?.focus());
    return () => cancelAnimationFrame(id);
  }, []);

  // Scroll the highlighted row into view.
  useEffect(() => {
    const el = listRef.current?.querySelector<HTMLElement>(`[data-idx="${safeIndex}"]`);
    el?.scrollIntoView?.({ block: 'nearest' });
  }, [safeIndex]);

  const commit = (idx: number) => {
    const hit = results[idx];
    if (hit) {
      onPick(hit.node);
      onClose();
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setActiveIndex(results.length ? (safeIndex + 1) % results.length : 0);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setActiveIndex(results.length ? (safeIndex - 1 + results.length) % results.length : 0);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      commit(safeIndex);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      onClose();
    }
    // Stop canvas-level shortcuts (Delete, Ctrl+Z, …) from firing while typing.
    e.stopPropagation();
  };

  return (
    <div
      role="dialog"
      aria-label="Search nodes"
      onMouseDown={(e) => {
        // Click on the backdrop closes; clicks inside the panel don't.
        if (e.target === e.currentTarget) onClose();
      }}
      style={{
        position: 'absolute',
        inset: 0,
        zIndex: 2000,
        display: 'flex',
        justifyContent: 'center',
        alignItems: 'flex-start',
        paddingTop: '12vh',
        background: 'rgba(0,0,0,0.35)',
        backdropFilter: 'blur(2px)',
      }}
    >
      <div
        style={{
          width: 'min(520px, 90%)',
          background: 'var(--bg-surface-opaque, #101625)',
          border: '1px solid var(--border-color)',
          borderRadius: '12px',
          boxShadow: '0 20px 60px rgba(0,0,0,0.5)',
          overflow: 'hidden',
        }}
      >
        <input
          ref={inputRef}
          value={query}
          onChange={(e) => {
            setQuery(e.target.value);
            setActiveIndex(0);
          }}
          onKeyDown={handleKeyDown}
          placeholder="Jump to node…"
          aria-label="Search nodes by name"
          style={{
            width: '100%',
            boxSizing: 'border-box',
            padding: '14px 18px',
            fontSize: '0.95rem',
            background: 'transparent',
            border: 'none',
            borderBottom: '1px solid var(--border-color)',
            color: 'var(--text-primary, #e5e7eb)',
            outline: 'none',
          }}
        />
        <div ref={listRef} style={{ maxHeight: '320px', overflowY: 'auto' }}>
          {results.length === 0 ? (
            <div style={{ padding: '16px 18px', color: 'var(--text-secondary)', fontSize: '0.85rem' }}>
              No matching nodes
            </div>
          ) : (
            results.map((r, idx) => (
              <div
                key={r.node.id}
                data-idx={idx}
                role="button"
                tabIndex={-1}
                onMouseEnter={() => setActiveIndex(idx)}
                onMouseDown={(e) => {
                  e.preventDefault(); // keep focus in the input
                  commit(idx);
                }}
                style={{
                  display: 'flex',
                  justifyContent: 'space-between',
                  gap: '12px',
                  padding: '10px 18px',
                  cursor: 'pointer',
                  background: idx === safeIndex ? 'var(--color-accent, #3b82f6)' : 'transparent',
                  color: idx === safeIndex ? '#fff' : 'var(--text-primary, #e5e7eb)',
                  fontSize: '0.88rem',
                }}
              >
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {r.label}
                </span>
                <span style={{ flexShrink: 0, opacity: 0.6, fontSize: '0.75rem' }}>{r.node.type}</span>
              </div>
            ))
          )}
        </div>
      </div>
    </div>
  );
}
