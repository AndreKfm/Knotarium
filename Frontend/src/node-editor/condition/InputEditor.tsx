// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Inline input editor (slice 2) — opens from an input node. Tabbed Reference / Literal:
//   • Reference: pick an upstream variable (typed, from the variable store). Because Phase 3 runs on
//     "manual sample only", the chosen reference also gets an inline SAMPLE value field so the live
//     graph can resolve it (later phases swap manual samples for last-run/dry-run).
//   • Literal: a string / number / boolean segmented control; boolean is a true/false toggle, the
//     others a text input. The value is held as draft text and coerced to its type on Save.
// Pure presentational — the parent owns dismissal/anchor and the operand/sample state.

import { useEffect, useRef, useState } from 'react';
import type { OperandType } from './conditionEval';
import type { DraftOperand } from './conditionModel';

/** A pickable upstream reference: a label, its declared type, and the expression to persist. */
export interface RefOption {
  id: string;
  label: string;
  type: OperandType;
  ref: string;
  group?: string;
}

export interface InputEditorProps {
  operand: DraftOperand;
  variables: RefOption[];
  /** Current manual sample for the operand's ref (when it's a reference). */
  sampleValue?: unknown;
  /**
   * True for the B operand of a list-right op ('Is one of' …): the literal is a comma-separated LIST,
   * not a single typed value. The type picker is hidden (elements are typed against A at eval time) and
   * the input is held as raw 'string' text.
   */
  isList?: boolean;
  onChangeOperand: (operand: DraftOperand) => void;
  onChangeSample: (value: unknown) => void;
  onClose: () => void;
}

const LITERAL_TYPES: OperandType[] = ['string', 'number', 'boolean'];

export function InputEditor({ operand, variables, sampleValue, isList = false, onChangeOperand, onChangeSample, onClose }: InputEditorProps) {
  const [tab, setTab] = useState<'ref' | 'lit'>(operand.kind === 'ref' ? 'ref' : 'lit');
  const ref = useRef<HTMLDivElement>(null);

  // Close on a click outside the popover. The trigger input card (.cne-input) is excluded so clicking it
  // toggles via its own handler (and clicking ANOTHER input card just switches the open target), instead
  // of this fighting that click. Added after mount, so the opening click never triggers it.
  useEffect(() => {
    const onDown = (e: MouseEvent) => {
      const target = e.target as Element | null;
      if (ref.current && !ref.current.contains(target) && !target?.closest('.cne-input')) onClose();
    };
    // Capture phase: the React Flow canvas stops pane mousedowns from bubbling to document, so a
    // bubble-phase listener never sees a click on empty canvas. Capture fires before that.
    document.addEventListener('mousedown', onDown, true);
    return () => document.removeEventListener('mousedown', onDown, true);
  }, [onClose]);

  return (
    <div
      ref={ref}
      className="cne-ied"
      onKeyDown={(e) => {
        if (e.key === 'Escape') onClose();
      }}
    >
      <div className="cne-ied-tabs" role="tablist">
        <button type="button" role="tab" aria-selected={tab === 'ref'} className={tab === 'ref' ? 'cne-tab-on' : ''} onClick={() => setTab('ref')}>
          Reference
        </button>
        <button type="button" role="tab" aria-selected={tab === 'lit'} className={tab === 'lit' ? 'cne-tab-on' : ''} onClick={() => setTab('lit')}>
          Literal
        </button>
      </div>

      {tab === 'ref' ? (
        <div className="cne-ied-ref">
          {variables.length === 0 ? (
            <div className="cne-ied-empty">No upstream variables available.</div>
          ) : (
            <ul className="cne-ied-reflist">
              {variables.map((v) => {
                const selected = operand.kind === 'ref' && operand.ref === v.ref;
                return (
                  <li key={v.id}>
                    <button
                      type="button"
                      className={`cne-ied-refrow ${selected ? 'cne-ied-refrow-on' : ''}`}
                      onClick={() => onChangeOperand({ kind: 'ref', type: v.type, ref: v.ref })}
                    >
                      <span className="cne-diamond" data-type={v.type} aria-hidden />
                      <span className="cne-ied-refpath">{v.label}</span>
                      <span className="cne-ied-reftype">{v.type}</span>
                    </button>
                  </li>
                );
              })}
            </ul>
          )}

          {operand.kind === 'ref' && operand.ref.trim().length > 0 && (
            <label className="cne-ied-sample">
              <span>Sample value</span>
              <input
                type="text"
                aria-label="Sample value"
                value={sampleValue === undefined || sampleValue === null ? '' : String(sampleValue)}
                onChange={(e) => onChangeSample(e.target.value)}
                placeholder="value for live preview…"
              />
            </label>
          )}
        </div>
      ) : isList ? (
        <div className="cne-ied-lit">
          <input
            type="text"
            aria-label="List values"
            className="cne-ied-litinput"
            value={operand.kind === 'lit' ? operand.text : ''}
            onChange={(e) => onChangeOperand({ kind: 'lit', type: 'string', text: e.target.value })}
            placeholder="e.g. 3, 5, 4"
          />
          <div className="cne-ied-listhint">
            Comma-separated values — matches if the input equals any one of them.
          </div>
        </div>
      ) : (
        <div className="cne-ied-lit">
          <div className="cne-segment cne-ied-typeseg" role="group" aria-label="Literal type">
            {LITERAL_TYPES.map((t) => (
              <button
                key={t}
                type="button"
                aria-pressed={operand.type === t}
                className={operand.type === t ? 'cne-seg-on' : ''}
                onClick={() =>
                  onChangeOperand(
                    operand.kind === 'lit'
                      ? { kind: 'lit', type: t, text: t === 'boolean' && operand.text !== 'true' && operand.text !== 'false' ? 'true' : operand.text }
                      : { kind: 'lit', type: t, text: t === 'boolean' ? 'true' : '' },
                  )
                }
              >
                {t}
              </button>
            ))}
          </div>

          {operand.type === 'boolean' ? (
            <div className="cne-segment" role="group" aria-label="Boolean value">
              {(['true', 'false'] as const).map((bv) => {
                const text = operand.kind === 'lit' ? operand.text : '';
                return (
                  <button
                    key={bv}
                    type="button"
                    aria-pressed={text === bv}
                    className={text === bv ? 'cne-seg-on' : ''}
                    onClick={() => onChangeOperand({ kind: 'lit', type: 'boolean', text: bv })}
                  >
                    {bv}
                  </button>
                );
              })}
            </div>
          ) : (
            <input
              type={operand.type === 'number' ? 'text' : 'text'}
              inputMode={operand.type === 'number' ? 'decimal' : 'text'}
              aria-label="Literal value"
              className="cne-ied-litinput"
              value={operand.kind === 'lit' ? operand.text : ''}
              onChange={(e) => onChangeOperand({ kind: 'lit', type: operand.type, text: e.target.value })}
              placeholder={operand.type === 'number' ? '0' : 'text…'}
            />
          )}
        </div>
      )}
    </div>
  );
}
