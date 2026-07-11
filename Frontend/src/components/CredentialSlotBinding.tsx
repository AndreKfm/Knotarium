import { TI } from './templateIcons';
import type { CredentialSummary, TemplateCredentialSlot } from '../types';

interface CredentialSlotBindingProps {
  slots: TemplateCredentialSlot[];
  credentials: CredentialSummary[];
  bindings: Record<string, string>;
  onChange: (bindings: Record<string, string>) => void;
}

/**
 * Renders one design-spec bind row per declared credential slot: an amber key tile that turns into a
 * green check once bound, the slot label · kind, the mono `slot:` token (amber → green), and a credential
 * picker. Shared by the importer and gallery so binding looks and behaves identically everywhere.
 */
export function CredentialSlotBinding({ slots, credentials, bindings, onChange }: CredentialSlotBindingProps) {
  if (slots.length === 0) {
    return null;
  }

  const setBinding = (slot: string, value: string) => {
    const next = { ...bindings };
    if (value) {
      next[slot] = value;
    } else {
      delete next[slot];
    }
    onChange(next);
  };

  return (
    <>
      {slots.map((slot) => {
        const bound = bindings[slot.slot];
        return (
          <div className={`bind${bound ? ' ok' : ''}`} key={slot.slot}>
            <div className="b-left">
              <span className="b-ic">{bound ? TI.check({ width: 16, height: 16 }) : TI.key({ width: 16, height: 16 })}</span>
              <div style={{ minWidth: 0 }}>
                <div className="b-name">
                  {slot.displayName}
                  {slot.requiredCredentialType && (
                    <span style={{ color: 'var(--faint)', fontWeight: 500 }}> · {slot.requiredCredentialType}</span>
                  )}
                </div>
                <div className="b-tok mono" style={{ color: bound ? 'var(--green)' : 'var(--amber)' }}>slot:{slot.slot}</div>
                {slot.description && (
                  <div style={{ fontSize: 11.5, color: 'var(--faint)', marginTop: 2 }}>{slot.description}</div>
                )}
              </div>
            </div>
            <div className="bsel">
              <select
                className={bound ? '' : 'unset'}
                aria-label={`Bind credential for slot ${slot.slot}`}
                value={bound ?? ''}
                onChange={(e) => setBinding(slot.slot, e.target.value)}
              >
                <option value="">Choose credential…</option>
                {credentials.map((c) => <option key={c.id} value={c.id}>{c.name} ({c.id})</option>)}
              </select>
              <span className="chev">{TI.chev()}</span>
            </div>
          </div>
        );
      })}
    </>
  );
}
