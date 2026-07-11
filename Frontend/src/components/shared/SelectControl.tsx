/**
 * SelectControl.tsx
 * ---------------------------------------------------------------------------
 * Knot Garden's canonical select-control primitive (the same visual language as
 * OperationPicker): a 44px control with a violet open-ring, a portal dropdown menu
 * with optional live search, optional color-coded HTTP method badges, grouped
 * options, and a trailing check on the selected row. Generalized so it can drive a
 * plain icon+label select (Server Config), a badge+path select (Collection), or an
 * async searchable select with loading/error/empty states (Selection).
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';

export interface SelectControlOption {
  value: string;
  label: string;
  meta?: string;
  group?: string;
  /** HTTP method — renders a color-coded badge (get/post/put/del/patch). */
  badge?: string;
  /** Render the label as a path, highlighting {param} segments. */
  labelIsPath?: boolean;
}

interface SelectControlProps {
  options: SelectControlOption[];
  value: string | null | undefined;
  onChange: (value: string) => void;
  placeholder?: string;
  /** Leading icon for the control / option rows when there is no badge. */
  leadingIcon?: React.ReactNode;
  searchable?: boolean;
  searchPlaceholder?: string;
  disabled?: boolean;
  /** Async states (Selection field). */
  loading?: boolean;
  error?: string | null;
  emptyText?: string;
  onReload?: () => void;
}

const METHOD_CLASS: Record<string, string> = {
  get: 'get', post: 'post', put: 'put', delete: 'del', del: 'del', patch: 'patch',
};
const methodClass = (m?: string) => (m ? METHOD_CLASS[m.toLowerCase()] ?? 'other' : 'other');
const methodLabel = (m: string) => (m.toLowerCase() === 'delete' ? 'DEL' : m.toUpperCase());

function renderPath(path: string) {
  return path.split(/(\{[^}]+\})/g).map((part, i) =>
    /^\{[^}]+\}$/.test(part) ? <span key={i} className="rsc-param">{part}</span> : <span key={i}>{part}</span>,
  );
}

const CSS = `
.rsc { position: relative; }
.rsc-control { display: flex; align-items: center; gap: 11px; width: 100%; min-height: 44px;
  background: #0e141d; border: 1.5px solid #212b39; border-radius: 11px; padding: 10px 13px; cursor: pointer;
  transition: border-color .15s, box-shadow .15s; text-align: left; }
.rsc-control:hover { border-color: #2c3a4c; }
.rsc-control.rsc-open { border-color: #7c6cf0; box-shadow: 0 0 0 4px rgba(124,108,240,0.13); }
.rsc-control:disabled { opacity: .5; cursor: not-allowed; }
.rsc-ico { flex: none; display: inline-flex; color: #6b7888; }
.rsc-main { min-width: 0; flex: 1; display: flex; align-items: baseline; gap: 9px; }
.rsc-label { font-size: 14px; font-weight: 600; color: #e6edf3; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.rsc-label.rsc-mono { font-family: ui-monospace, "SF Mono", Menlo, monospace; }
.rsc-meta { font-size: 12px; color: #44505f; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
  font-family: ui-monospace, Menlo, monospace; }
.rsc-placeholder { font-size: 14px; color: #5d6675; }
.rsc-caret { margin-left: auto; flex: none; color: #6b7888; display: inline-flex; transition: transform .16s ease; }
.rsc-control.rsc-open .rsc-caret { transform: rotate(180deg); }
.rsc-param { color: #c3b9ff; }

.rsc-badge { display: inline-grid; place-items: center; min-width: 50px; height: 20px; padding: 0 8px; flex: none;
  border-radius: 6px; font-family: ui-monospace, Menlo, monospace; font-size: 10px; font-weight: 700; letter-spacing: 0.04em; }
.rsc-badge.get  { color: #38bdf8; background: rgba(56,189,248,0.13);  border: 1px solid rgba(56,189,248,0.32); }
.rsc-badge.post { color: #34d399; background: rgba(52,211,153,0.13);  border: 1px solid rgba(52,211,153,0.32); }
.rsc-badge.put  { color: #f0b429; background: rgba(240,180,41,0.13);  border: 1px solid rgba(240,180,41,0.32); }
.rsc-badge.del  { color: #f0556d; background: rgba(240,85,109,0.13);  border: 1px solid rgba(240,85,109,0.32); }
.rsc-badge.patch{ color: #c084fc; background: rgba(192,132,252,0.13); border: 1px solid rgba(192,132,252,0.32); }
.rsc-badge.other{ color: #6b7888; background: rgba(133,147,166,0.12); border: 1px solid rgba(133,147,166,0.3); }

.rsc-menu { position: fixed; z-index: 4000; display: flex; flex-direction: column;
  background: linear-gradient(180deg, #121a25, #0e141d); border: 1.5px solid #212b39; border-radius: 13px;
  overflow: hidden; box-shadow: 0 40px 90px -30px rgba(0,0,0,0.95); padding: 6px; }
.rsc-search { display: flex; align-items: center; gap: 9px; padding: 9px 11px; margin: -6px -6px 6px;
  border-bottom: 1px solid #1b2430; flex: none; }
.rsc-search svg { color: #44505f; flex: none; }
.rsc-search input { flex: 1; background: none; border: none; outline: none; color: #e6edf3; font-family: inherit; font-size: 13.5px; }
.rsc-search input::placeholder { color: #44505f; }
.rsc-refresh { background: none; border: none; color: #6b7888; cursor: pointer; display: inline-flex; padding: 2px; border-radius: 6px; }
.rsc-refresh:hover { color: #c3b9ff; }

.rsc-list { flex: 1; min-height: 0; overflow-y: auto; max-height: 320px; }
.rsc-list::-webkit-scrollbar { width: 10px; }
.rsc-list::-webkit-scrollbar-thumb { background: #1f2937; border-radius: 8px; border: 3px solid #0e141d; }
.rsc-group { font-size: 10px; font-weight: 800; letter-spacing: 0.09em; color: #44505f; text-transform: uppercase; padding: 10px 11px 5px; }
.rsc-opt { display: flex; align-items: center; gap: 11px; padding: 9px 11px; border-radius: 9px; cursor: pointer; }
.rsc-opt:hover, .rsc-opt.rsc-active { background: #121a25; }
.rsc-opt.rsc-sel { background: rgba(124,108,240,0.14); box-shadow: inset 0 0 0 1px rgba(124,108,240,0.3); }
.rsc-omain { min-width: 0; flex: 1; display: flex; align-items: baseline; gap: 8px; }
.rsc-olabel { font-size: 13.5px; font-weight: 600; color: #e6edf3; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.rsc-olabel.rsc-mono { font-family: ui-monospace, "SF Mono", Menlo, monospace; }
.rsc-ometa { font-size: 12px; color: #44505f; white-space: nowrap; font-family: ui-monospace, Menlo, monospace; }
.rsc-opt.rsc-sel .rsc-olabel { color: #fff; }
.rsc-check { margin-left: auto; flex: none; color: #7c6cf0; opacity: 0; display: inline-flex; }
.rsc-opt.rsc-sel .rsc-check { opacity: 1; }
.rsc-state { padding: 22px 14px; text-align: center; font-size: 13px; color: #5d6675; }
.rsc-state.rsc-err { color: #f0556d; }
`;

let injected = false;
function useInjectStyles() {
  useEffect(() => {
    if (injected) return;
    const el = document.createElement('style');
    el.setAttribute('data-knot-select-control', '');
    el.textContent = CSS;
    document.head.appendChild(el);
    injected = true;
  }, []);
}

const Caret = (
  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><path d="M6 9l6 6 6-6" /></svg>
);
const Search = (
  <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><circle cx="11" cy="11" r="7" /><path d="M21 21l-4.3-4.3" /></svg>
);
const Check = (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round"><path d="M20 6L9 17l-5-5" /></svg>
);
const Refresh = (
  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M21 12a9 9 0 1 1-2.6-6.4M21 3v6h-6" /></svg>
);

export function SelectControl({
  options, value, onChange, placeholder = 'Select…', leadingIcon, searchable, searchPlaceholder = 'Search…',
  disabled, loading, error, emptyText = 'No options.', onReload,
}: SelectControlProps) {
  useInjectStyles();
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [rect, setRect] = useState<DOMRect | null>(null);
  const controlRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);

  const selected = useMemo(() => options.find((o) => o.value === value) ?? null, [options, value]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return options;
    return options.filter((o) => `${o.label} ${o.meta ?? ''} ${o.group ?? ''} ${o.badge ?? ''}`.toLowerCase().includes(q));
  }, [options, query]);

  const grouped = useMemo(() => {
    const map = new Map<string, SelectControlOption[]>();
    for (const o of filtered) {
      const g = o.group ?? '';
      if (!map.has(g)) map.set(g, []);
      map.get(g)!.push(o);
    }
    return Array.from(map.entries());
  }, [filtered]);

  const updateRect = useCallback(() => {
    if (controlRef.current) setRect(controlRef.current.getBoundingClientRect());
  }, []);

  const openMenu = useCallback(() => {
    if (disabled) return;
    updateRect();
    setQuery('');
    setOpen(true);
  }, [disabled, updateRect]);

  useEffect(() => {
    if (!open) return;
    const t = setTimeout(() => searchRef.current?.focus(), 0);
    return () => clearTimeout(t);
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const reposition = () => updateRect();
    const onDown = (e: MouseEvent) => {
      if (controlRef.current?.contains(e.target as Node)) return;
      if (menuRef.current?.contains(e.target as Node)) return;
      setOpen(false);
    };
    window.addEventListener('scroll', reposition, true);
    window.addEventListener('resize', reposition);
    document.addEventListener('mousedown', onDown);
    return () => {
      window.removeEventListener('scroll', reposition, true);
      window.removeEventListener('resize', reposition);
      document.removeEventListener('mousedown', onDown);
    };
  }, [open, updateRect]);

  const choose = (v: string) => { onChange(v); setOpen(false); };

  const GAP = 7;
  let menuStyle: React.CSSProperties = { visibility: 'hidden' };
  if (rect) {
    const spaceBelow = window.innerHeight - rect.bottom;
    const spaceAbove = rect.top;
    const openUp = spaceBelow < 280 && spaceAbove > spaceBelow;
    menuStyle = openUp
      ? { left: rect.left, width: rect.width, bottom: window.innerHeight - rect.top + GAP }
      : { left: rect.left, width: rect.width, top: rect.bottom + GAP };
  }

  return (
    <div className="rsc">
      <button
        type="button" ref={controlRef} disabled={disabled}
        className={'rsc-control' + (open ? ' rsc-open' : '')}
        onClick={() => (open ? setOpen(false) : openMenu())}
      >
        {selected?.badge ? (
          <span className={'rsc-badge ' + methodClass(selected.badge)}>{methodLabel(selected.badge)}</span>
        ) : leadingIcon ? (
          <span className="rsc-ico">{leadingIcon}</span>
        ) : null}
        {selected ? (
          <span className="rsc-main">
            <span className={'rsc-label' + (selected.labelIsPath ? ' rsc-mono' : '')}>
              {selected.labelIsPath ? renderPath(selected.label) : selected.label}
            </span>
            {selected.meta && <span className="rsc-meta">{selected.meta}</span>}
          </span>
        ) : (
          <span className="rsc-main"><span className="rsc-placeholder">{placeholder}</span></span>
        )}
        <span className="rsc-caret">{Caret}</span>
      </button>

      {open && rect && createPortal(
        <div className="rsc-menu" ref={menuRef} style={menuStyle}>
          {searchable && (
            <div className="rsc-search">
              {Search}
              <input
                ref={searchRef} type="text" value={query} placeholder={searchPlaceholder}
                onChange={(e) => setQuery(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Escape') setOpen(false); }}
                autoComplete="off"
              />
              {onReload && (
                <button type="button" className="rsc-refresh" title="Refresh" onClick={() => onReload()}>{Refresh}</button>
              )}
            </div>
          )}
          <div className="rsc-list">
            {loading ? (
              <div className="rsc-state">Loading…</div>
            ) : error ? (
              <div className="rsc-state rsc-err">{error}</div>
            ) : filtered.length === 0 ? (
              <div className="rsc-state">{query ? `No matches for “${query}”.` : emptyText}</div>
            ) : (
              grouped.map(([group, opts]) => (
                <div key={group || '_'}>
                  {group && <div className="rsc-group">{group}</div>}
                  {opts.map((o) => (
                    <div
                      key={o.value}
                      className={'rsc-opt' + (o.value === value ? ' rsc-sel' : '')}
                      onClick={() => choose(o.value)}
                    >
                      {o.badge && <span className={'rsc-badge ' + methodClass(o.badge)}>{methodLabel(o.badge)}</span>}
                      <span className="rsc-omain">
                        <span className={'rsc-olabel' + (o.labelIsPath ? ' rsc-mono' : '')}>
                          {o.labelIsPath ? renderPath(o.label) : o.label}
                        </span>
                        {o.meta && <span className="rsc-ometa">{o.meta}</span>}
                      </span>
                      <span className="rsc-check">{Check}</span>
                    </div>
                  ))}
                </div>
              ))
            )}
          </div>
        </div>,
        document.body,
      )}
    </div>
  );
}
