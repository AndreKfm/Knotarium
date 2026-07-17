// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useMemo, useState } from 'react';
import {
  buildCron,
  parseCron,
  describeCron,
  listTimeZones,
  DEFAULT_CRON_SPEC,
  FREQ_OPTIONS,
  WEEKDAY_LABELS,
  type CronFreq,
  type CronSpec,
} from '../utils/cronSchedule';

export interface SchedulerPropertyFormProps {
  properties: Record<string, unknown>;
  onChange: (properties: Record<string, unknown>) => void;
}

const fieldStyle: React.CSSProperties = {
  width: '100%',
  padding: '10px',
  borderRadius: '8px',
  background: 'rgba(0, 0, 0, 0.2)',
  border: '1px solid var(--border-color)',
  color: '#fff',
  fontSize: '0.85rem',
  outline: 'none',
  boxSizing: 'border-box',
};

const selectStyle: React.CSSProperties = { ...fieldStyle, background: 'var(--bg-surface-opaque)', colorScheme: 'dark' };

const labelStyle: React.CSSProperties = {
  display: 'block',
  fontSize: '0.75rem',
  fontWeight: 700,
  color: 'var(--text-secondary)',
  textTransform: 'uppercase',
  marginBottom: '6px',
};

const fieldWrapStyle: React.CSSProperties = { display: 'flex', flexDirection: 'column', gap: '6px' };

function ModeTab({ active, children, onClick }: { active: boolean; children: React.ReactNode; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      style={{
        flex: 1,
        padding: '7px 10px',
        borderRadius: '7px',
        border: '1px solid ' + (active ? 'var(--color-accent)' : 'var(--border-color)'),
        background: active ? 'rgba(99, 102, 241, 0.16)' : 'transparent',
        color: active ? '#fff' : 'var(--text-secondary)',
        fontSize: '0.78rem',
        fontWeight: 600,
        cursor: 'pointer',
      }}
    >
      {children}
    </button>
  );
}

export function SchedulerPropertyForm({ properties, onChange }: SchedulerPropertyFormProps) {
  const cronExpression = typeof properties.cronExpression === 'string' ? properties.cronExpression : '';
  const timezoneId = typeof properties.timezoneId === 'string' ? properties.timezoneId : '';

  // The current cron either maps onto the simple builder or is a "custom" expression that only the
  // advanced field can edit. Default new/simple schedules to the friendly builder; drop to advanced when
  // the expression can't be represented by the builder so we never silently rewrite it.
  const parsed = useMemo(() => parseCron(cronExpression), [cronExpression]);
  const [mode, setMode] = useState<'simple' | 'advanced'>(
    cronExpression && !parsed ? 'advanced' : 'simple',
  );

  const timeZones = useMemo(() => {
    const zones = listTimeZones();
    // Keep whatever is already configured selectable even if the runtime doesn't list it.
    return timezoneId && !zones.includes(timezoneId) ? [timezoneId, ...zones] : zones;
  }, [timezoneId]);

  const spec: CronSpec = parsed ?? DEFAULT_CRON_SPEC;
  const summary = describeCron(cronExpression);

  const setCron = (next: string) => onChange({ ...properties, cronExpression: next });
  const updateSpec = (patch: Partial<CronSpec>) => setCron(buildCron({ ...spec, ...patch }));
  const setTimezone = (next: string) => onChange({ ...properties, timezoneId: next });

  const timeValue = `${String(spec.hour).padStart(2, '0')}:${String(spec.minute).padStart(2, '0')}`;
  const onTimeChange = (value: string) => {
    const [h, m] = value.split(':').map((n) => Number.parseInt(n, 10));
    updateSpec({ hour: Number.isFinite(h) ? h : 0, minute: Number.isFinite(m) ? m : 0 });
  };

  const showTime = spec.freq === 'daily' || spec.freq === 'weekdays' || spec.freq === 'weekly' || spec.freq === 'monthly';

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
      {/* Mode toggle */}
      <div style={{ display: 'flex', gap: '6px' }}>
        <ModeTab active={mode === 'simple'} onClick={() => setMode('simple')}>Simple</ModeTab>
        <ModeTab active={mode === 'advanced'} onClick={() => setMode('advanced')}>Advanced (cron)</ModeTab>
      </div>

      {mode === 'simple' ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '14px' }}>
          {!parsed && cronExpression ? (
            <p style={{ fontSize: '0.76rem', color: 'var(--color-warning)', margin: 0 }}>
              This expression (<code>{cronExpression}</code>) is a custom schedule. Switch to Advanced to edit it,
              or pick a frequency below to replace it.
            </p>
          ) : null}

          <div style={fieldWrapStyle}>
            <label style={labelStyle}>Frequency</label>
            <select
              value={spec.freq}
              onChange={(e) => updateSpec({ freq: e.target.value as CronFreq })}
              style={selectStyle}
            >
              {FREQ_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value} style={{ background: 'var(--bg-surface-opaque)', color: '#fff' }}>
                  {opt.label}
                </option>
              ))}
            </select>
          </div>

          {spec.freq === 'minutes' && (
            <div style={fieldWrapStyle}>
              <label style={labelStyle}>Every (minutes)</label>
              <input
                type="number"
                min={1}
                max={59}
                value={spec.every}
                onChange={(e) => updateSpec({ every: Number.parseInt(e.target.value, 10) || 1 })}
                style={fieldStyle}
              />
            </div>
          )}

          {spec.freq === 'hourly' && (
            <div style={fieldWrapStyle}>
              <label style={labelStyle}>At minute</label>
              <input
                type="number"
                min={0}
                max={59}
                value={spec.minute}
                onChange={(e) => updateSpec({ minute: Number.parseInt(e.target.value, 10) || 0 })}
                style={fieldStyle}
              />
            </div>
          )}

          {spec.freq === 'weekly' && (
            <div style={fieldWrapStyle}>
              <label style={labelStyle}>On day</label>
              <select
                value={spec.dow}
                onChange={(e) => updateSpec({ dow: Number.parseInt(e.target.value, 10) })}
                style={selectStyle}
              >
                {WEEKDAY_LABELS.map((label, i) => (
                  <option key={label} value={i} style={{ background: 'var(--bg-surface-opaque)', color: '#fff' }}>
                    {label}
                  </option>
                ))}
              </select>
            </div>
          )}

          {spec.freq === 'monthly' && (
            <div style={fieldWrapStyle}>
              <label style={labelStyle}>On day of month</label>
              <input
                type="number"
                min={1}
                max={31}
                value={spec.dom}
                onChange={(e) => updateSpec({ dom: Number.parseInt(e.target.value, 10) || 1 })}
                style={fieldStyle}
              />
            </div>
          )}

          {showTime && (
            <div style={fieldWrapStyle}>
              <label style={labelStyle}>At time</label>
              <input type="time" value={timeValue} onChange={(e) => onTimeChange(e.target.value)} style={fieldStyle} />
            </div>
          )}

          <div style={{ fontSize: '0.74rem', color: 'var(--text-muted)', fontFamily: 'ui-monospace, Menlo, monospace' }}>
            cron: <span style={{ color: 'var(--color-accent)' }}>{cronExpression || buildCron(spec)}</span>
          </div>
        </div>
      ) : (
        <div style={fieldWrapStyle}>
          <label style={labelStyle}>Cron expression *</label>
          <input
            type="text"
            value={cronExpression}
            onChange={(e) => setCron(e.target.value)}
            placeholder="0 8 * * 1-5"
            style={{ ...fieldStyle, fontFamily: 'ui-monospace, Menlo, monospace' }}
          />
          <span style={{ fontSize: '0.72rem', color: 'var(--text-muted)' }}>
            5 fields: <code>minute hour day-of-month month day-of-week</code> (Sun=0). Use <code>*</code> for any,
            <code> */5</code> for steps, <code>1-5</code> for ranges.
          </span>
          {summary && (
            <span style={{ fontSize: '0.76rem', color: 'var(--color-success)' }}>✓ {summary}</span>
          )}
        </div>
      )}

      {/* Timezone combobox: pick from suggestions or type any IANA zone. */}
      <div style={fieldWrapStyle}>
        <label style={labelStyle}>Time zone *</label>
        <input
          type="text"
          list="scheduler-timezones"
          value={timezoneId}
          onChange={(e) => setTimezone(e.target.value)}
          placeholder="Europe/Berlin"
          style={fieldStyle}
        />
        <datalist id="scheduler-timezones">
          {timeZones.map((tz) => (
            <option key={tz} value={tz} />
          ))}
        </datalist>
      </div>
    </div>
  );
}
