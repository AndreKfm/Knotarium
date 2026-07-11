import { useEffect, useRef, useState } from 'react';
import { TI } from './templateIcons';

export interface SelectOption {
  value: string;
  label: string;
}

interface TemplateSelectProps {
  value: string;
  options: SelectOption[];
  placeholder?: string;
  ariaLabel: string;
  onChange: (value: string) => void;
}

/**
 * A dark, design-matched dropdown for the Templates screen — replaces the native <select> whose
 * popup rendered with the OS's light styling. Accessible: trigger is a `combobox`, the list is a
 * `listbox` of `option`s; closes on outside-click and Escape; basic arrow-key navigation.
 */
export function TemplateSelect({ value, options, placeholder = 'Select…', ariaLabel, onChange }: TemplateSelectProps) {
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const rootRef = useRef<HTMLDivElement>(null);

  const selected = options.find((o) => o.value === value);

  useEffect(() => {
    if (!open) return;
    const onDocClick = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDocClick);
    return () => document.removeEventListener('mousedown', onDocClick);
  }, [open]);

  const choose = (v: string) => { onChange(v); setOpen(false); };

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') { setOpen(false); return; }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (!open) { setOpen(true); return; }
      setActive((i) => {
        const next = e.key === 'ArrowDown' ? i + 1 : i - 1;
        return Math.max(0, Math.min(options.length - 1, next));
      });
      return;
    }
    if ((e.key === 'Enter' || e.key === ' ') && open) {
      e.preventDefault();
      const opt = options[active];
      if (opt) choose(opt.value);
    }
  };

  return (
    <div className="tpl-select" ref={rootRef}>
      <button
        type="button"
        className={`tpl-select-trigger${open ? ' open' : ''}`}
        role="combobox"
        aria-label={ariaLabel}
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        onKeyDown={onKeyDown}
      >
        <span className={selected ? '' : 'placeholder'}>{selected ? selected.label : placeholder}</span>
        <span className={`tpl-select-chev${open ? ' open' : ''}`}>{TI.chev()}</span>
      </button>
      {open && (
        <div className="tpl-select-menu" role="listbox" aria-label={ariaLabel}>
          {options.length === 0 ? (
            <div className="tpl-select-empty">No workflows available.</div>
          ) : (
            options.map((o, i) => (
              <button
                type="button"
                key={o.value}
                role="option"
                aria-selected={o.value === value}
                className={`tpl-select-option${o.value === value ? ' on' : ''}${i === active ? ' active' : ''}`}
                onMouseEnter={() => setActive(i)}
                onClick={() => choose(o.value)}
              >
                <span className="tpl-select-option-label">{o.label}</span>
                {o.value === value && <span className="tpl-select-check">{TI.check()}</span>}
              </button>
            ))
          )}
        </div>
      )}
    </div>
  );
}
