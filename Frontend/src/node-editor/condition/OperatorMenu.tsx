// Inline operator picker (slice 2) — opens from a comparator's operator pill. Type-aware: only
// operators valid for the left operand's known type are listed (operatorsForType). Grouped + searchable,
// marks unary ops and the current selection, and enforces the edit-time ordering guards: a cross-type
// ordering op is shown disabled (checkOrderingTypes), and an ordinal-string hint is surfaced so the
// author isn't surprised by lexical comparison. Pure presentational — the parent owns dismissal/anchor.

import { useEffect, useRef, useState } from 'react';
import { Check } from 'lucide-react';
import { OPERATOR_GROUPS, type OperatorGroup } from './operators';
import { checkOrderingTypes, operatorsForType, ordinalStringHint, type KnownType } from './operatorFilter';

export interface OperatorMenuProps {
  currentOp: string;
  leftType: KnownType;
  rightType: KnownType;
  onPick: (op: string) => void;
  onClose: () => void;
}

export function OperatorMenu({ currentOp, leftType, rightType, onPick, onClose }: OperatorMenuProps) {
  const [query, setQuery] = useState('');
  const ql = query.trim().toLowerCase();
  const ref = useRef<HTMLDivElement>(null);

  // Close on a click outside the menu; the trigger pill (.cne-op-pill) is excluded so it toggles itself.
  useEffect(() => {
    const onDown = (e: MouseEvent) => {
      const target = e.target as Element | null;
      if (ref.current && !ref.current.contains(target) && !target?.closest('.cne-op-pill')) onClose();
    };
    // Capture phase: React Flow stops pane mousedowns bubbling to document, so a bubble listener misses
    // empty-canvas clicks. Capture fires first.
    document.addEventListener('mousedown', onDown, true);
    return () => document.removeEventListener('mousedown', onDown, true);
  }, [onClose]);

  const available = operatorsForType(leftType).filter(
    (o) => !ql || `${o.label} ${o.symbol} ${o.group}`.toLowerCase().includes(ql),
  );

  const placeholder = leftType === 'any' ? 'Search operators…' : `${leftType} operators…`;
  const hint = ordinalStringHint(currentOp, leftType, rightType);

  return (
    <div
      ref={ref}
      className="cne-menu"
      role="menu"
      onKeyDown={(e) => {
        if (e.key === 'Escape') onClose();
      }}
    >
      <div className="cne-menu-search">
        <input
          autoFocus
          type="text"
          aria-label="Search operators"
          placeholder={placeholder}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        {leftType !== 'any' && <span className="cne-menu-typetag">{leftType}</span>}
      </div>

      <div className="cne-menu-list">
        {OPERATOR_GROUPS.map((group: OperatorGroup) => {
          const rows = available.filter((o) => o.group === group);
          if (rows.length === 0) return null;
          return (
            <div key={group} className="cne-menu-group">
              <div className="cne-menu-grouphdr">{group}</div>
              {rows.map((o) => {
                const block = checkOrderingTypes(o.id, leftType, rightType);
                const isCurrent = o.id === currentOp;
                return (
                  <button
                    key={o.id}
                    type="button"
                    role="menuitemradio"
                    aria-checked={isCurrent}
                    className={`cne-menu-row ${isCurrent ? 'cne-menu-row-on' : ''}`}
                    disabled={block.blocked}
                    aria-label={o.label}
                    title={block.blocked ? block.reason : o.label}
                    onClick={() => {
                      if (!block.blocked) onPick(o.id);
                    }}
                  >
                    <span className="cne-op-symbol">{o.symbol}</span>
                    <span className="cne-menu-label">{o.label}</span>
                    {o.arity === 'unary' && <span className="cne-menu-unary">1 input</span>}
                    {isCurrent && <Check size={13} className="cne-menu-check" />}
                  </button>
                );
              })}
            </div>
          );
        })}
        {available.length === 0 && <div className="cne-menu-empty">No matching operators.</div>}
      </div>

      {hint && <div className="cne-menu-hint">{hint}</div>}
    </div>
  );
}
