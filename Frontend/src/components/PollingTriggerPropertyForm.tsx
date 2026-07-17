// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useState } from 'react';
import { api } from '../utils/api';
import { FieldDropWrapper } from './shared/ManifestForm';
import { listServerConfigs } from '../utils/serverConfigClient';
import { listSpecs, getSpecDetail } from '../utils/openApiClient';
import type { ImportedSpec, ServerConfigInfo, SpecDetail } from '../types';

// ─── Types ───────────────────────────────────────────────────────────────────

interface CredentialItem {
  id: string;
  name: string;
}

export interface PollingTriggerPropertyFormProps {
  workflowId?: string | null;
  properties: Record<string, unknown>;
  onChange: (properties: Record<string, unknown>) => void;
}

// ─── Constants ───────────────────────────────────────────────────────────────

const SOURCE_KINDS = ['http', 'openapi'] as const;
type SourceKind = (typeof SOURCE_KINDS)[number];

const CHANGE_DETECTIONS = ['etag', 'last-modified', 'hash', 'json-cursor', 'always'] as const;
type ChangeDetection = (typeof CHANGE_DETECTIONS)[number];

// ─── Shared field styles (mirrors ManifestForm / siblings) ───────────────────

const fieldStyle: React.CSSProperties = {
  width: '100%',
  padding: '10px',
  borderRadius: '8px',
  background: 'rgba(0, 0, 0, 0.2)',
  border: '1px solid var(--border-color)',
  color: '#fff',
  fontSize: '0.85rem',
  outline: 'none',
};

const selectStyle: React.CSSProperties = {
  ...fieldStyle,
  background: 'var(--bg-surface-opaque)',
};

const labelStyle: React.CSSProperties = {
  display: 'block',
  fontSize: '0.75rem',
  fontWeight: 700,
  color: 'var(--text-secondary)',
  textTransform: 'uppercase',
};

const fieldWrapStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: '6px',
};

const sectionHeadStyle: React.CSSProperties = {
  fontSize: '0.8rem',
  fontWeight: 700,
  color: 'var(--color-accent)',
  textTransform: 'uppercase',
  marginBottom: '10px',
  borderBottom: '1px solid var(--border-color)',
  paddingBottom: '4px',
};

// ─── Form component ──────────────────────────────────────────────────────────

export function PollingTriggerPropertyForm({
  workflowId,
  properties,
  onChange,
}: PollingTriggerPropertyFormProps) {
  const [credentials, setCredentials] = useState<CredentialItem[]>([]);
  const [serverConfigs, setServerConfigs] = useState<ServerConfigInfo[]>([]);
  const [specs, setSpecs] = useState<ImportedSpec[]>([]);
  const [specDetail, setSpecDetail] = useState<SpecDetail | null>(null);
  // Pure UI state: which spec's operations to display. NOT persisted to properties.
  const [selectedSpecId, setSelectedSpecId] = useState<string>('');
  const [operationsLoading, setOperationsLoading] = useState(false);

  // ── Derived property reads ───────────────────────────────────────────────
  const intervalSeconds = (properties.intervalSeconds as number) ?? 60;
  const sourceKind = ((properties.sourceKind as string) || 'http') as SourceKind;
  const changeDetection = ((properties.changeDetection as string) || 'hash') as ChangeDetection;
  const jsonCursorPath = (properties.jsonCursorPath as string) || '';

  // HTTP-source fields
  const url = (properties.url as string) || '';
  const method = (properties.method as string) || 'GET';
  const headersJson = (properties.headersJson as string) || '';
  const apiKeySecretRef = (properties.apiKeySecretRef as string) || '';

  // OpenAPI-source fields
  const serverConfigId = (properties.serverConfigId as string) || '';
  const operationId = (properties.operationId as string) || '';

  // ── Helper: write one property key ──────────────────────────────────────
  const set = (key: string, value: unknown) =>
    onChange({ ...properties, [key]: value });

  // ── Load credentials (always needed when sourceKind === 'http') ──────────
  useEffect(() => {
    api
      .getCredentials()
      .then((res) => setCredentials(res as CredentialItem[]))
      .catch((err) => console.error('Error loading credentials:', err));
  }, []);

  // ── Load server configs + specs (needed when sourceKind === 'openapi') ───
  useEffect(() => {
    listServerConfigs()
      .then(setServerConfigs)
      .catch((err) => console.error('Error loading server configs:', err));
    listSpecs()
      .then(setSpecs)
      .catch((err) => console.error('Error loading specs:', err));
  }, []);

  // ── Initialize selectedSpecId from saved operationId when specs load ─────
  // When the form is reopened with an already-persisted operationId, scan all
  // specs to find which one contains that operationId so the dropdowns reflect
  // the saved state. Runs once after specs are loaded (and whenever operationId
  // changes, e.g. on first load if specs arrive before operationId is read).
  useEffect(() => {
    if (!operationId || specs.length === 0) return;
    // If we already have a selectedSpecId that contains this operation, keep it
    if (selectedSpecId) return;

    let cancelled = false;

    // Scan each spec's detail to find which one owns the saved operationId
    void (async () => {
      for (const spec of specs) {
        try {
          const detail = await getSpecDetail(spec.id);
          const found = detail.groups
            .flatMap((g) => g.operations)
            .some((op) => op.operationId === operationId);
          if (found) {
            if (!cancelled) setSelectedSpecId(spec.id);
            return;
          }
        } catch {
          // skip specs that fail to load
        }
      }
    })();

    return () => { cancelled = true; };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  // selectedSpecId is intentionally excluded: this effect only runs to initialise
  // the spec selection while it is still empty; including it would re-trigger the
  // scan every time the user changes the spec dropdown.
  }, [specs, operationId]);

  // ── Load spec detail to enumerate operations ─────────────────────────────
  // Driven by selectedSpecId (pure UI state), NOT by a persisted property.
  useEffect(() => {
    if (!selectedSpecId) {
      setSpecDetail(null);
      return;
    }

    let cancelled = false;
    setOperationsLoading(true);
    getSpecDetail(selectedSpecId)
      .then((detail) => { if (!cancelled) setSpecDetail(detail); })
      .catch(() => { if (!cancelled) setSpecDetail(null); })
      .finally(() => { if (!cancelled) setOperationsLoading(false); });

    return () => { cancelled = true; };
  }, [selectedSpecId]);

  // Flatten all operations from spec detail for the operationId picker
  const operations = specDetail
    ? specDetail.groups.flatMap((g) => g.operations)
    : [];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '20px' }}>

      {/* ── Poll Interval ────────────────────────────────────────────────── */}
      <div style={fieldWrapStyle}>
        <label style={labelStyle}>
          Interval (seconds) <span style={{ color: 'var(--color-error)' }}>*</span>
        </label>
        <input
          type="number"
          value={intervalSeconds}
          min={1}
          onChange={(e) =>
            set('intervalSeconds', e.target.value ? parseFloat(e.target.value) : null)
          }
          placeholder="60"
          style={fieldStyle}
        />
      </div>

      {/* ── Source Kind ──────────────────────────────────────────────────── */}
      <div style={fieldWrapStyle}>
        <label style={labelStyle}>
          Source Kind <span style={{ color: 'var(--color-error)' }}>*</span>
        </label>
        <select
          value={sourceKind}
          onChange={(e) => set('sourceKind', e.target.value)}
          style={selectStyle}
        >
          {SOURCE_KINDS.map((k) => (
            <option key={k} value={k}>
              {k}
            </option>
          ))}
        </select>
      </div>

      {/* ── Change Detection ─────────────────────────────────────────────── */}
      <div style={fieldWrapStyle}>
        <label style={labelStyle}>
          Change Detection <span style={{ color: 'var(--color-error)' }}>*</span>
        </label>
        <select
          value={changeDetection}
          onChange={(e) => set('changeDetection', e.target.value)}
          style={selectStyle}
        >
          {CHANGE_DETECTIONS.map((v) => (
            <option key={v} value={v}>
              {v}
            </option>
          ))}
        </select>
      </div>

      {/* ── JSON Cursor Path (shown only when changeDetection === 'json-cursor') */}
      {changeDetection === 'json-cursor' && (
        <div style={fieldWrapStyle}>
          <label style={labelStyle}>
            JSON Cursor Path <span style={{ color: 'var(--color-error)' }}>*</span>
          </label>
          <FieldDropWrapper
            workflowId={workflowId}
            value={jsonCursorPath}
            onChange={(val) => set('jsonCursorPath', val)}
          >
            <input
              type="text"
              value={jsonCursorPath}
              onChange={(e) => set('jsonCursorPath', e.target.value)}
              placeholder="e.g. $.data.cursor"
              style={fieldStyle}
            />
          </FieldDropWrapper>
          <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)', marginTop: '2px' }}>
            JSONPath expression pointing to the cursor/timestamp field in the response.
          </span>
        </div>
      )}

      {/* ════════════════════════════════════════════════════════════════════
          HTTP source fields
          ════════════════════════════════════════════════════════════════════ */}
      {sourceKind === 'http' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '14px' }}>
          <h3 style={sectionHeadStyle}>HTTP Source</h3>

          {/* URL */}
          <div style={fieldWrapStyle}>
            <label style={labelStyle}>
              URL <span style={{ color: 'var(--color-error)' }}>*</span>
            </label>
            <FieldDropWrapper
              workflowId={workflowId}
              value={url}
              onChange={(val) => set('url', val)}
            >
              <input
                type="text"
                value={url}
                onChange={(e) => set('url', e.target.value)}
                placeholder="https://api.example.com/resource"
                style={fieldStyle}
              />
            </FieldDropWrapper>
            <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)', marginTop: '2px' }}>
              Supports expressions: <code>{'{{ $variables.myVar }}'}</code>
            </span>
          </div>

          {/* Method */}
          <div style={fieldWrapStyle}>
            <label style={labelStyle}>Method</label>
            <select
              value={method}
              onChange={(e) => set('method', e.target.value)}
              style={selectStyle}
            >
              {['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD'].map((m) => (
                <option key={m} value={m}>
                  {m}
                </option>
              ))}
            </select>
          </div>

          {/* Headers JSON */}
          <div style={fieldWrapStyle}>
            <label style={labelStyle}>Headers (JSON)</label>
            <FieldDropWrapper
              workflowId={workflowId}
              value={headersJson}
              onChange={(val) => set('headersJson', val)}
            >
              <textarea
                value={headersJson}
                onChange={(e) => set('headersJson', e.target.value)}
                placeholder={'{"Authorization": "Bearer token"}'}
                rows={4}
                style={{
                  ...fieldStyle,
                  fontFamily: 'monospace',
                  resize: 'vertical',
                }}
              />
            </FieldDropWrapper>
            <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)', marginTop: '2px' }}>
              Optional JSON object of extra request headers.
            </span>
          </div>

          {/* API Key Secret Ref — credential dropdown (same as ManifestForm credentialRef) */}
          <div style={fieldWrapStyle}>
            <label style={labelStyle}>API Key Secret (credential)</label>
            <select
              value={apiKeySecretRef}
              onChange={(e) => set('apiKeySecretRef', e.target.value)}
              style={selectStyle}
            >
              <option value="">Select credential...</option>
              {credentials.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name} ({c.id})
                </option>
              ))}
            </select>
            <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)', marginTop: '2px' }}>
              Optional — credential whose secret value is injected as the{' '}
              <code>Authorization</code> header.
            </span>
          </div>
        </div>
      )}

      {/* ════════════════════════════════════════════════════════════════════
          OpenAPI source fields
          ════════════════════════════════════════════════════════════════════ */}
      {sourceKind === 'openapi' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: '14px' }}>
          <h3 style={sectionHeadStyle}>OpenAPI Source</h3>

          {/* Server Config (reuse same dropdown pattern as ResourcePickerPropertyForm) */}
          <div style={fieldWrapStyle}>
            <label style={labelStyle}>
              Server Config <span style={{ color: 'var(--color-error)' }}>*</span>
            </label>
            <select
              value={serverConfigId}
              onChange={(e) => set('serverConfigId', e.target.value)}
              style={selectStyle}
            >
              <option value="">Select server config...</option>
              {serverConfigs.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name} ({c.baseUrl})
                </option>
              ))}
            </select>
          </div>

          {/* API Spec — pure UI state, drives the operation list only; NOT persisted */}
          <div style={fieldWrapStyle}>
            <label style={labelStyle}>
              API Spec <span style={{ color: 'var(--color-error)' }}>*</span>
            </label>
            <select
              value={selectedSpecId}
              onChange={(e) => {
                // Update local UI state only; reset operationId in persisted properties
                setSelectedSpecId(e.target.value);
                set('operationId', '');
              }}
              style={selectStyle}
            >
              <option value="">Select API spec...</option>
              {specs.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.title}
                </option>
              ))}
            </select>
          </div>

          {/* Operation ID */}
          <div style={fieldWrapStyle}>
            <label style={labelStyle}>
              Operation <span style={{ color: 'var(--color-error)' }}>*</span>
            </label>
            {operationsLoading ? (
              <span style={{ fontSize: '0.8rem', color: 'var(--text-muted)' }}>
                Loading operations…
              </span>
            ) : selectedSpecId && operations.length === 0 ? (
              <span style={{ fontSize: '0.8rem', color: 'var(--text-muted)' }}>
                No operations found
              </span>
            ) : (
              <select
                value={operationId}
                onChange={(e) => set('operationId', e.target.value)}
                style={selectStyle}
              >
                <option value="">Select operation...</option>
                {operations.map((op) => (
                  <option key={op.operationId} value={op.operationId}>
                    {op.method.toUpperCase()} {op.pathTemplate} ({op.operationId})
                  </option>
                ))}
              </select>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
