// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

/**
 * OperationPicker.tsx
 * ---------------------------------------------------------------------------
 * Custom replacement for the native <select> used to pick an OpenAPI operation
 * on a REST caller node. Color-coded method badges, monospace paths with
 * highlighted {params}, operationId + summary per row, grouped by resource tag,
 * with live search and full keyboard navigation. The dropdown renders through a
 * portal with fixed positioning so it is never clipped by the scrollable
 * properties panel.
 */

import { useState, useRef, useEffect, useMemo, useCallback } from 'react';
import { createPortal } from 'react-dom';
import type { OperationGroup, ApiOperation } from '../types';

interface OperationPickerProps {
  groups: OperationGroup[];
  value: string;
  onChange: (operationId: string) => void;
  disabled?: boolean;
}

const METHOD_META: Record<string, { label: string; cls: string }> = {
  get: { label: 'GET', cls: 'get' },
  post: { label: 'POST', cls: 'post' },
  put: { label: 'PUT', cls: 'put' },
  delete: { label: 'DELETE', cls: 'del' },
  patch: { label: 'PATCH', cls: 'patch' },
  head: { label: 'HEAD', cls: 'other' },
  options: { label: 'OPTIONS', cls: 'other' },
  trace: { label: 'TRACE', cls: 'other' },
};

function methodMeta(m: string) {
  return METHOD_META[(m || '').toLowerCase()] ?? { label: (m || '?').toUpperCase(), cls: 'other' };
}

/* highlight {params} inside a path template */
function renderPath(path: string) {
  return path.split(/(\{[^}]+\})/g).map((part, i) =>
    /^\{[^}]+\}$/.test(part)
      ? <span key={i} className="kop-param">{part}</span>
      : <span key={i}>{part}</span>
  );
}

/* ----------------------------- styles ----------------------------- */
const CSS = `
.kop-control { display: flex; align-items: center; gap: 11px; width: 100%;
  background: #0f141d; border: 1.5px solid #212b39; border-radius: 11px;
  padding: 11px 13px; cursor: pointer; transition: border-color .15s, box-shadow .15s; text-align: left; }
.kop-control:hover { border-color: #2f3d52; }
.kop-control.kop-open { border-color: #7c6cf0; box-shadow: 0 0 0 4px rgba(124,108,240,0.13); }
.kop-control:disabled { opacity: .5; cursor: not-allowed; }
.kop-sc-path { font-family: ui-monospace, "SF Mono", Menlo, monospace; font-size: 13.5px; font-weight: 600;
  color: #e6edf3; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.kop-sc-op { font-size: 12px; color: #8593a6; font-family: ui-monospace, Menlo, monospace; flex: none; }
.kop-sc-placeholder { font-size: 13.5px; color: #5d6675; }
.kop-caret { margin-left: auto; color: #8593a6; display: inline-flex; flex: none; transition: transform .16s ease; }
.kop-control.kop-open .kop-caret { transform: rotate(180deg); }

.kop-badge { display: inline-grid; place-items: center; min-width: 52px; height: 21px; padding: 0 8px; flex: none;
  border-radius: 6px; font-family: ui-monospace, "SF Mono", Menlo, monospace; font-size: 10.5px;
  font-weight: 700; letter-spacing: 0.04em; }
.kop-badge.get  { color: #38bdf8; background: rgba(56,189,248,0.13);  border: 1px solid rgba(56,189,248,0.32); }
.kop-badge.post { color: #34d399; background: rgba(52,211,153,0.13);  border: 1px solid rgba(52,211,153,0.32); }
.kop-badge.put  { color: #f0b429; background: rgba(240,180,41,0.13);  border: 1px solid rgba(240,180,41,0.32); }
.kop-badge.del  { color: #f0556d; background: rgba(240,85,109,0.13);  border: 1px solid rgba(240,85,109,0.32); }
.kop-badge.patch{ color: #c084fc; background: rgba(192,132,252,0.13); border: 1px solid rgba(192,132,252,0.32); }
.kop-badge.other{ color: #8593a6; background: rgba(133,147,166,0.12); border: 1px solid rgba(133,147,166,0.3); }

.kop-menu { position: fixed; z-index: 4000; display: flex; flex-direction: column;
  background: linear-gradient(180deg, #131922, #0f141d);
  border: 1.5px solid #212b39; border-radius: 14px; overflow: hidden;
  box-shadow: 0 50px 100px -30px rgba(0,0,0,0.95), 0 0 0 1px rgba(124,108,240,0.06); }

.kop-search { display: flex; align-items: center; gap: 10px; padding: 13px 15px;
  border-bottom: 1px solid #1b2430; flex: none; }
.kop-search svg { flex: none; color: #5d6675; }
.kop-search input { flex: 1; background: none; border: none; outline: none; color: #e6edf3;
  font-family: inherit; font-size: 14px; }
.kop-search input::placeholder { color: #5d6675; }
.kop-search kbd { font-family: ui-monospace, Menlo, monospace; font-size: 10.5px; color: #5d6675;
  border: 1px solid #212b39; border-radius: 5px; padding: 2px 6px; background: #0f141d; }

.kop-list { flex: 1; min-height: 0; overflow-y: auto; padding: 6px; }
.kop-list::-webkit-scrollbar { width: 10px; }
.kop-list::-webkit-scrollbar-thumb { background: #1f2937; border-radius: 8px; border: 3px solid #0f141d; }
.kop-list::-webkit-scrollbar-track { background: transparent; }

.kop-group { font-size: 10px; font-weight: 800; letter-spacing: 0.1em; color: #5d6675;
  text-transform: uppercase; padding: 12px 12px 6px; display: flex; align-items: center; gap: 8px; }
.kop-group .kop-count { color: #41495a; font-weight: 700; }
.kop-group::after { content: ""; flex: 1; height: 1px; background: #1b2430; }

.kop-opt { display: flex; align-items: center; gap: 12px; padding: 9px 11px; border-radius: 9px;
  cursor: pointer; position: relative; }
.kop-opt-body { min-width: 0; flex: 1; }
.kop-opt-path { display: block; font-family: ui-monospace, "SF Mono", Menlo, monospace; font-size: 13px;
  font-weight: 600; color: #e6edf3; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.kop-param { color: #a99bff; }
.kop-opt-desc { font-size: 12px; color: #8593a6; margin-top: 3px; overflow: hidden;
  text-overflow: ellipsis; white-space: nowrap; }
.kop-check { margin-left: auto; flex: none; color: #7c6cf0; opacity: 0; display: inline-flex; }

.kop-opt:hover, .kop-opt.kop-active { background: #131922; }
.kop-opt.kop-active { box-shadow: inset 0 0 0 1px #212b39; }
.kop-opt.kop-active::before { content: ""; position: absolute; left: 0; top: 8px; bottom: 8px;
  width: 3px; border-radius: 0 3px 3px 0; background: var(--kop-accent, #8593a6); }
.kop-opt.kop-selected { background: rgba(124,108,240,0.14); box-shadow: inset 0 0 0 1px rgba(124,108,240,0.3); }
.kop-opt.kop-selected::before { content: ""; position: absolute; left: 0; top: 8px; bottom: 8px;
  width: 3px; border-radius: 0 3px 3px 0; background: #7c6cf0; }
.kop-opt.kop-selected .kop-check { opacity: 1; }
.kop-opt.kop-selected .kop-opt-path { color: #fff; }

.kop-empty { padding: 30px; text-align: center; color: #5d6675; font-size: 13px; }

.kop-foot { display: flex; align-items: center; gap: 16px; padding: 10px 15px; flex: none;
  border-top: 1px solid #1b2430; background: rgba(9,12,19,0.5); }
.kop-foot .kop-hint { display: flex; align-items: center; gap: 6px; font-size: 11px; color: #5d6675; }
.kop-foot kbd { font-family: ui-monospace, Menlo, monospace; font-size: 10px; color: #8593a6;
  border: 1px solid #212b39; border-radius: 5px; padding: 2px 5px; background: #0f141d; min-width: 18px; text-align: center; }
.kop-foot .kop-spacer { margin-left: auto; }
.kop-foot .kop-rc { font-size: 11px; color: #5d6675; }
.kop-foot .kop-rc b { color: #8593a6; }
`;

let stylesInjected = false;
function useInjectStyles() {
  useEffect(() => {
    if (stylesInjected) return;
    const el = document.createElement('style');
    el.setAttribute('data-knot-op-picker', '');
    el.textContent = CSS;
    document.head.appendChild(el);
    stylesInjected = true;
  }, []);
}

const IconSearch = (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><circle cx="11" cy="11" r="7" /><path d="M21 21l-4.3-4.3" /></svg>
);
const IconCaret = (
  <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><path d="M6 9l6 6 6-6" /></svg>
);
const IconCheck = (
  <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round"><path d="M20 6L9 17l-5-5" /></svg>
);

const ACCENT: Record<string, string> = {
  get: '#38bdf8', post: '#34d399', put: '#f0b429', del: '#f0556d', patch: '#c084fc', other: '#8593a6',
};

export function OperationPicker({ groups, value, onChange, disabled }: OperationPickerProps) {
  useInjectStyles();
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [activeIdx, setActiveIdx] = useState(0);
  const [rect, setRect] = useState<DOMRect | null>(null);

  const controlRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  const totalOps = useMemo(() => groups.reduce((n, g) => n + g.operations.length, 0), [groups]);

  const selected = useMemo(() => {
    for (const g of groups) {
      const op = g.operations.find(o => o.operationId === value);
      if (op) return op;
    }
    return null;
  }, [groups, value]);

  // filtered groups + flat list (in display order) for keyboard nav.
  // flatIndex maps each op to its position in `flat` so rows don't each do an O(n) indexOf.
  const { filteredGroups, flat, flatIndex } = useMemo(() => {
    const q = query.trim().toLowerCase();
    const match = (o: ApiOperation) =>
      !q || `${o.pathTemplate} ${o.operationId} ${o.summary ?? ''} ${o.method}`.toLowerCase().includes(q);
    const fg: { tag: string; ops: ApiOperation[] }[] = [];
    const fl: ApiOperation[] = [];
    const idx = new Map<ApiOperation, number>();
    for (const g of groups) {
      const ops = g.operations.filter(match);
      if (ops.length) {
        fg.push({ tag: g.tag, ops });
        for (const op of ops) idx.set(op, fl.push(op) - 1);
      }
    }
    return { filteredGroups: fg, flat: fl, flatIndex: idx };
  }, [groups, query]);

  const updateRect = useCallback(() => {
    if (controlRef.current) setRect(controlRef.current.getBoundingClientRect());
  }, []);

  const openMenu = useCallback(() => {
    if (disabled) return;
    updateRect();
    setQuery('');
    setOpen(true);
  }, [disabled, updateRect]);

  const closeMenu = useCallback(() => setOpen(false), []);

  // focus search + reset active when opened
  useEffect(() => {
    if (!open) return;
    const t = setTimeout(() => searchRef.current?.focus(), 0);
    return () => clearTimeout(t);
  }, [open]);

  // when results change, default active to the selected op (no query) or first
  useEffect(() => {
    if (!open) return;
    if (query) { setActiveIdx(0); return; }
    const i = flat.findIndex(o => o.operationId === value);
    setActiveIdx(i >= 0 ? i : 0);
  }, [open, query, flat, value]);

  // reposition on scroll / resize while open; close on outside click / esc
  useEffect(() => {
    if (!open) return;
    const onScroll = () => updateRect();
    const onResize = () => updateRect();
    const onDocMouseDown = (e: MouseEvent) => {
      if (controlRef.current?.contains(e.target as Node)) return;
      if (menuRef.current?.contains(e.target as Node)) return;
      closeMenu();
    };
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onResize);
    document.addEventListener('mousedown', onDocMouseDown);
    return () => {
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onResize);
      document.removeEventListener('mousedown', onDocMouseDown);
    };
  }, [open, updateRect, closeMenu]);

  // keep the active row scrolled into view
  useEffect(() => {
    if (!open || !listRef.current) return;
    const el = listRef.current.querySelector<HTMLElement>(`[data-idx="${activeIdx}"]`);
    if (el && typeof el.scrollIntoView === 'function') el.scrollIntoView({ block: 'nearest' });
  }, [activeIdx, open]);

  const choose = (op: ApiOperation) => {
    onChange(op.operationId);
    closeMenu();
  };

  const onSearchKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActiveIdx(i => Math.min(i + 1, flat.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActiveIdx(i => Math.max(i - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); if (flat[activeIdx]) choose(flat[activeIdx]); }
    else if (e.key === 'Escape') { e.preventDefault(); closeMenu(); }
  };

  // ---- menu position (auto-flip up when there isn't room below) ----
  const GAP = 8;
  let menuStyle: React.CSSProperties = { visibility: 'hidden' };
  if (rect) {
    const spaceBelow = window.innerHeight - rect.bottom;
    const spaceAbove = rect.top;
    const openUp = spaceBelow < 320 && spaceAbove > spaceBelow;
    const maxHeight = Math.min(440, Math.max(180, (openUp ? spaceAbove : spaceBelow) - 16));
    menuStyle = openUp
      ? { left: rect.left, width: rect.width, bottom: window.innerHeight - rect.top + GAP, maxHeight }
      : { left: rect.left, width: rect.width, top: rect.bottom + GAP, maxHeight };
  }

  const sm = selected ? methodMeta(selected.method) : null;

  return (
    <>
      <button
        type="button"
        ref={controlRef}
        className={'kop-control' + (open ? ' kop-open' : '')}
        disabled={disabled}
        onClick={() => (open ? closeMenu() : openMenu())}
      >
        {selected && sm ? (
          <>
            <span className={'kop-badge ' + sm.cls}>{sm.label}</span>
            <span className="kop-sc-path">{renderPath(selected.pathTemplate)}</span>
            <span className="kop-sc-op">{selected.operationId}</span>
          </>
        ) : (
          <span className="kop-sc-placeholder">Select operation…</span>
        )}
        <span className="kop-caret">{IconCaret}</span>
      </button>

      {open && rect && createPortal(
        <div className="kop-menu" ref={menuRef} style={menuStyle}>
          <div className="kop-search">
            {IconSearch}
            <input
              ref={searchRef}
              type="text"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              onKeyDown={onSearchKeyDown}
              placeholder="Search operations, paths, or descriptions…"
              autoComplete="off"
            />
            <kbd>esc</kbd>
          </div>

          <div className="kop-list" ref={listRef}>
            {flat.length === 0 ? (
              <div className="kop-empty">No operations match “{query}”.</div>
            ) : (
              filteredGroups.map(g => (
                <div key={g.tag}>
                  <div className="kop-group">{g.tag} <span className="kop-count">{g.ops.length}</span></div>
                  {g.ops.map(op => {
                    const idx = flatIndex.get(op) ?? -1;
                    const m = methodMeta(op.method);
                    const isSelected = op.operationId === value;
                    const isActive = idx === activeIdx;
                    return (
                      <div
                        key={op.operationId}
                        data-idx={idx}
                        className={'kop-opt' + (isSelected ? ' kop-selected' : '') + (isActive ? ' kop-active' : '')}
                        style={{ ['--kop-accent' as string]: ACCENT[m.cls] }}
                        onClick={() => choose(op)}
                        onMouseMove={() => setActiveIdx(idx)}
                      >
                        <span className={'kop-badge ' + m.cls}>{m.label}</span>
                        <span className="kop-opt-body">
                          <span className="kop-opt-path">{renderPath(op.pathTemplate)}</span>
                          {op.summary && <span className="kop-opt-desc">{op.summary}</span>}
                        </span>
                        <span className="kop-check">{IconCheck}</span>
                      </div>
                    );
                  })}
                </div>
              ))
            )}
          </div>

          <div className="kop-foot">
            <span className="kop-hint"><kbd>↑</kbd><kbd>↓</kbd> navigate</span>
            <span className="kop-hint"><kbd>↵</kbd> select</span>
            <span className="kop-spacer" />
            {flat.length > 0 && (
              <span className="kop-rc"><b>{flat.length}</b> of {totalOps} operations</span>
            )}
          </div>
        </div>,
        document.body
      )}
    </>
  );
}
