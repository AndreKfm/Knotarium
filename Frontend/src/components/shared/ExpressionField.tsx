import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { findOpenExpression, type ExpressionCompletion } from '../../utils/expressionCompletions';

interface ExpressionFieldProps {
  value: string;
  onChange: (value: string) => void;
  /** All available candidates; the field filters them by the text typed after `{{`. */
  completions: ExpressionCompletion[];
  multiline?: boolean;
  placeholder?: string;
  rows?: number;
  style?: React.CSSProperties;
}

/**
 * A text/textarea field with `{{ }}` expression autocomplete. Typing `{{` opens a popover of the
 * workflow's referenceable variables/outputs (schema-driven, from {@link buildExpressionCompletions});
 * selecting one inserts the full `{{ … }}` expression. Arrow keys navigate, Enter/Tab insert, Esc closes.
 */
export function ExpressionField({ value, onChange, completions, multiline, placeholder, rows, style }: ExpressionFieldProps) {
  const inputRef = useRef<HTMLInputElement | HTMLTextAreaElement | null>(null);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [fragmentStart, setFragmentStart] = useState(0);
  const [highlight, setHighlight] = useState(0);
  const pendingCaret = useRef<number | null>(null);

  const filtered = open
    ? completions.filter((c) =>
        !query.trim()
        || c.label.toLowerCase().includes(query.trim().toLowerCase())
        || c.insertText.toLowerCase().includes(query.trim().toLowerCase()))
    : [];

  // Restore the caret after a controlled-value update (insertion / typing).
  useLayoutEffect(() => {
    if (pendingCaret.current !== null && inputRef.current) {
      inputRef.current.setSelectionRange(pendingCaret.current, pendingCaret.current);
      pendingCaret.current = null;
    }
  }, [value]);

  useEffect(() => { setHighlight(0); }, [query, open]);

  const syncPopover = (text: string, caret: number) => {
    const fragment = findOpenExpression(text, caret);
    if (fragment) {
      setOpen(true);
      setFragmentStart(fragment.start);
      setQuery(fragment.query);
    } else {
      setOpen(false);
    }
  };

  const handleChange = (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    const text = event.target.value;
    onChange(text);
    syncPopover(text, event.target.selectionStart ?? text.length);
  };

  const insert = (completion: ExpressionCompletion) => {
    const el = inputRef.current;
    const caret = el?.selectionStart ?? value.length;
    const next = value.slice(0, fragmentStart) + completion.insertText + value.slice(caret);
    pendingCaret.current = fragmentStart + completion.insertText.length;
    setOpen(false);
    onChange(next);
  };

  const handleKeyDown = (event: React.KeyboardEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    if (!open || filtered.length === 0) {
      return;
    }
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setHighlight((h) => (h + 1) % filtered.length);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setHighlight((h) => (h - 1 + filtered.length) % filtered.length);
    } else if (event.key === 'Enter' || event.key === 'Tab') {
      event.preventDefault();
      insert(filtered[Math.min(highlight, filtered.length - 1)]);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      setOpen(false);
    }
  };

  const sharedProps = {
    ref: inputRef as never,
    value,
    placeholder,
    onChange: handleChange,
    onKeyDown: handleKeyDown,
    onKeyUp: (e: React.KeyboardEvent<HTMLInputElement | HTMLTextAreaElement>) => {
      // Navigation keys move the caret without changing text — re-sync the popover.
      if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) {
        syncPopover(value, (e.target as HTMLInputElement).selectionStart ?? value.length);
      }
    },
    onBlur: () => window.setTimeout(() => setOpen(false), 120),
    style,
  };

  return (
    <div style={{ position: 'relative' }}>
      {multiline ? <textarea {...sharedProps} rows={rows ?? 4} /> : <input type="text" {...sharedProps} />}
      {open && filtered.length > 0 && (
        <div
          role="listbox"
          style={{
            position: 'absolute',
            top: '100%',
            left: 0,
            right: 0,
            zIndex: 30,
            marginTop: 4,
            maxHeight: 220,
            overflowY: 'auto',
            background: 'var(--bg-surface-opaque)',
            border: '1px solid var(--border-color)',
            borderRadius: 8,
            boxShadow: '0 12px 30px rgba(0,0,0,0.45)',
          }}
        >
          {filtered.map((completion, index) => (
            <button
              key={completion.insertText}
              type="button"
              role="option"
              aria-selected={index === highlight}
              // Use onMouseDown so the field's onBlur (which closes the popover) doesn't fire first.
              onMouseDown={(e) => { e.preventDefault(); insert(completion); }}
              onMouseEnter={() => setHighlight(index)}
              style={{
                display: 'flex',
                flexDirection: 'column',
                gap: 2,
                width: '100%',
                textAlign: 'left',
                padding: '7px 10px',
                background: index === highlight ? 'rgba(99,102,241,0.18)' : 'transparent',
                border: 'none',
                borderBottom: '1px solid rgba(255,255,255,0.04)',
                cursor: 'pointer',
              }}
            >
              <span style={{ fontFamily: 'ui-monospace, Menlo, monospace', fontSize: '0.8rem', color: '#e2e8f0' }}>{completion.label}</span>
              <span style={{ fontSize: '0.68rem', color: 'var(--text-muted)' }}>{completion.detail}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
