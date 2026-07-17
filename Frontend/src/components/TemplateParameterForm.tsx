// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { TI } from './templateIcons';
import type { ParameterValues, TemplateParameter } from '../types';

interface TemplateParameterFormProps {
  parameters: TemplateParameter[];
  values: ParameterValues;
  onChange: (values: ParameterValues) => void;
}

/** The effective value for a parameter: the supplied value, else its declared default, else empty. */
export function effectiveParamValue(parameter: TemplateParameter, values: ParameterValues): string {
  const supplied = values[parameter.key];
  if (supplied !== undefined && supplied !== '') return supplied;
  return parameter.default ?? '';
}

/** Prefills a values map from the parameters' declared defaults — call when opening an install/insert form. */
export function defaultParamValues(parameters: TemplateParameter[]): ParameterValues {
  const out: ParameterValues = {};
  for (const p of parameters) {
    if (p.default != null && p.default !== '') out[p.key] = p.default;
  }
  return out;
}

/** True when every required parameter has a non-empty effective value — the install/insert gate. */
export function paramsSatisfied(parameters: TemplateParameter[], values: ParameterValues): boolean {
  return parameters.every((p) => !p.required || effectiveParamValue(p, values).trim() !== '');
}

/**
 * Renders one typed input row per declared template parameter, reusing the credential-slot bind-row styling
 * so install/insert dialogs look consistent. Required-but-empty rows are flagged. Shared by the importer,
 * the gallery, the insert picker, and the user library.
 */
export function TemplateParameterForm({ parameters, values, onChange }: TemplateParameterFormProps) {
  if (parameters.length === 0) return null;

  const set = (key: string, value: string) => {
    const next = { ...values };
    if (value === '') delete next[key];
    else next[key] = value;
    onChange(next);
  };

  return (
    <>
      {parameters.map((p) => {
        const value = effectiveParamValue(p, values);
        const filled = value.trim() !== '';
        const missing = p.required && !filled;
        const inputId = `param-${p.key}`;
        return (
          <div className={`bind${filled ? ' ok' : ''}`} key={p.key}>
            <div className="b-left">
              <span className="b-ic">{filled ? TI.check({ width: 16, height: 16 }) : TI.sliders({ width: 16, height: 16 })}</span>
              <div style={{ minWidth: 0 }}>
                <div className="b-name">
                  {p.label}
                  {p.required && <span style={{ color: 'var(--amber)', fontWeight: 600 }}> *</span>}
                  <span style={{ color: 'var(--faint)', fontWeight: 500 }}> · {p.type}</span>
                </div>
                <div className="b-tok mono" style={{ color: filled ? 'var(--green)' : 'var(--amber)' }}>{`{{param:${p.key}}}`}</div>
                {p.description && (
                  <div style={{ fontSize: 11.5, color: 'var(--faint)', marginTop: 2 }}>{p.description}</div>
                )}
                {missing && (
                  <div style={{ fontSize: 11.5, color: 'var(--amber)', marginTop: 2 }}>Required.</div>
                )}
              </div>
            </div>
            <div className="bsel">
              {p.type === 'enum' ? (
                <>
                  <select id={inputId} aria-label={`Value for ${p.label}`} className={filled ? '' : 'unset'} value={value} onChange={(e) => set(p.key, e.target.value)}>
                    <option value="">Choose…</option>
                    {(p.options ?? []).map((opt) => <option key={opt} value={opt}>{opt}</option>)}
                  </select>
                  <span className="chev">{TI.chev()}</span>
                </>
              ) : p.type === 'boolean' ? (
                <>
                  <select id={inputId} aria-label={`Value for ${p.label}`} className={filled ? '' : 'unset'} value={value || 'false'} onChange={(e) => set(p.key, e.target.value)}>
                    <option value="true">true</option>
                    <option value="false">false</option>
                  </select>
                  <span className="chev">{TI.chev()}</span>
                </>
              ) : (
                <input
                  id={inputId}
                  className="param-inp"
                  type={p.type === 'number' ? 'number' : 'text'}
                  aria-label={`Value for ${p.label}`}
                  value={value}
                  placeholder={p.default ?? (p.type === 'number' ? '0' : '')}
                  onChange={(e) => set(p.key, e.target.value)}
                />
              )}
            </div>
          </div>
        );
      })}
    </>
  );
}
