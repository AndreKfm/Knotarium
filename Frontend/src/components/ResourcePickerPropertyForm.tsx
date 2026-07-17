// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useMemo, useState } from 'react';
import type { ImportedSpec, ParameterDefinition, ServerConfigInfo, SpecDetail } from '../types';
import { listServerConfigs } from '../utils/serverConfigClient';
import { listSpecs, getSpecDetail } from '../utils/openApiClient';
import { useNodeOptions } from '../hooks/useNodeOptions';
import { SelectControl, type SelectControlOption } from './shared/SelectControl';

interface ResourcePickerPropertyFormProps {
  workflowId?: string | null;
  properties: Record<string, unknown>;
  onChange: (properties: Record<string, unknown>) => void;
}

const CSS = `
.rpf-field { margin-top: 20px; }
.rpf-label { font-size: 11px; font-weight: 700; letter-spacing: 0.06em; color: #6b7888; text-transform: uppercase;
  margin-bottom: 9px; display: flex; align-items: center; gap: 5px; }
.rpf-label .rpf-req { color: #f0556d; }
.rpf-help { font-size: 11.5px; color: #44505f; margin-top: 8px; line-height: 1.5; }
.rpf-intro { font-size: 12px; color: #44505f; line-height: 1.55; margin: 0; }
.rpf-intro code { color: #c3b9ff; font-family: ui-monospace, Menlo, monospace; font-size: 11.5px; }

.rpf-input { width: 100%; min-height: 44px; background: #0e141d; border: 1.5px solid #212b39; border-radius: 11px;
  padding: 11px 13px; color: #e6edf3; font-family: ui-monospace, Menlo, monospace; font-size: 14px; font-weight: 500;
  transition: border-color .15s, box-shadow .15s; box-sizing: border-box; }
.rpf-input:focus { outline: none; border-color: #7c6cf0; box-shadow: 0 0 0 4px rgba(124,108,240,0.13); }

.rpf-disc { margin-top: 18px; border-radius: 12px; border: 1px solid #1b2430; background: rgba(14,20,29,0.5); overflow: hidden; }
.rpf-disc-head { display: flex; align-items: center; gap: 9px; padding: 12px 14px; cursor: pointer; user-select: none; }
.rpf-disc-chev { color: #44505f; display: inline-flex; transition: transform .18s; }
.rpf-disc.rpf-open .rpf-disc-chev { transform: rotate(90deg); }
.rpf-disc-title { font-size: 11px; font-weight: 700; letter-spacing: 0.06em; color: #6b7888; text-transform: uppercase; }
.rpf-disc-tag { margin-left: auto; font-size: 10px; color: #44505f; font-weight: 600; }
.rpf-disc-body { padding: 2px 14px 14px; }
.rpf-disc-note { font-size: 11.5px; color: #44505f; line-height: 1.5; margin: 0 0 8px; }
.rpf-map { display: flex; align-items: center; gap: 10px; padding: 9px 0; border-top: 1px solid #1b2430; }
.rpf-map:first-of-type { border-top: 0; }
.rpf-map-src { flex: 1; min-width: 0; background: #0e141d; border: 1px solid #212b39; border-radius: 8px; padding: 7px 9px;
  color: #9fb0c2; font-family: ui-monospace, Menlo, monospace; font-size: 12.5px; outline: none; }
.rpf-map-src:focus { border-color: #7c6cf0; }
.rpf-map-arrow { color: #44505f; flex: none; display: inline-flex; }
.rpf-map-role { font-size: 11px; font-weight: 700; letter-spacing: 0.03em; padding: 3px 9px; border-radius: 7px; flex: none;
  color: #c3b9ff; background: rgba(124,108,240,0.14); border: 1px solid rgba(124,108,240,0.3); }

.rpf-divider { height: 1px; background: #1b2430; margin: 22px 0 4px; }

.rpf-preview { margin-top: 11px; border-radius: 11px; border: 1px solid #1b2430; background: #080c12; overflow: hidden; }
.rpf-preview-bar { display: flex; align-items: center; gap: 8px; padding: 9px 12px; border-bottom: 1px solid #1b2430; }
.rpf-preview-bar .rpf-pl { font-size: 10px; font-weight: 700; letter-spacing: 0.06em; color: #44505f; text-transform: uppercase; }
.rpf-preview-bar .rpf-live { margin-left: auto; font-size: 11px; font-weight: 600; }
.rpf-json { padding: 10px 12px; font-family: ui-monospace, Menlo, monospace; font-size: 12px; line-height: 1.6; color: #9fb0c2; }
.rpf-json .k { color: #c3b9ff; }
.rpf-json .s { color: #87e8a8; }
.rpf-placeholder { font-size: 12.5px; color: #44505f; margin: 0; }
`;

let injected = false;
function useInjectStyles() {
  useEffect(() => {
    if (injected) return;
    const el = document.createElement('style');
    el.setAttribute('data-knot-resource-picker-form', '');
    el.textContent = CSS;
    document.head.appendChild(el);
    injected = true;
  }, []);
}

const IconServers = (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><rect x="3" y="4" width="18" height="7" rx="2" /><rect x="3" y="13" width="18" height="7" rx="2" /><path d="M7 7.5h.01M7 16.5h.01" /></svg>
);
const IconLink = (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M10 13a5 5 0 0 0 7 0l3-3a5 5 0 0 0-7-7l-1 1" /><path d="M14 11a5 5 0 0 0-7 0l-3 3a5 5 0 0 0 7 7l1-1" /></svg>
);
const IconChevR = (
  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round"><path d="M9 6l6 6-6 6" /></svg>
);
const IconArrow = (
  <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><path d="M5 12h14M13 6l6 6-6 6" /></svg>
);
const IconCheckSmall = (
  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M20 6L9 17l-5-5" /></svg>
);

interface StoredSingle { value: string; label?: string; mode: 'list' | 'manual'; }
function readSelection(value: unknown): StoredSingle | null {
  if (value == null || value === '') return null;
  if (typeof value === 'string') return { value, mode: 'manual' };
  if (typeof value === 'object') {
    const o = value as Record<string, unknown>;
    if (typeof o.value === 'string') {
      return { value: o.value, label: typeof o.label === 'string' ? o.label : undefined, mode: o.mode === 'manual' ? 'manual' : 'list' };
    }
  }
  return null;
}

function isCollectionPath(pathTemplate: string): boolean {
  const segs = pathTemplate.split('/').filter(Boolean);
  return segs.length > 0 && !segs[segs.length - 1].startsWith('{');
}
const stripLeadingSlash = (p: string) => (p.startsWith('/') ? p.slice(1) : p);

export function ResourcePickerPropertyForm({ properties, onChange }: ResourcePickerPropertyFormProps) {
  useInjectStyles();
  const [serverConfigs, setServerConfigs] = useState<ServerConfigInfo[]>([]);
  const [specs, setSpecs] = useState<ImportedSpec[]>([]);
  const [specDetail, setSpecDetail] = useState<SpecDetail | null>(null);
  const [showAdvanced, setShowAdvanced] = useState(false);

  useEffect(() => {
    listServerConfigs().then(setServerConfigs).catch((e) => console.error('server configs', e));
    listSpecs().then(setSpecs).catch((e) => console.error('specs', e));
  }, []);

  const serverConfigId = (properties.serverConfigId as string) || '';
  const specId = (properties.pickerSpecId as string) || '';
  const collectionOp = (properties.pickerCollectionOp as string) || '';
  const path = (properties.path as string) || '';
  const labelField = (properties.labelField as string) || '';
  const valueField = (properties.valueField as string) || '';
  const collectionField = (properties.collectionField as string) || '';

  useEffect(() => {
    if (!specId) { setSpecDetail(null); return; }
    getSpecDetail(specId).then(setSpecDetail).catch(() => setSpecDetail(null));
  }, [specId]);

  const set = (key: string, value: unknown) => onChange({ ...properties, [key]: value });

  // ── Options for each control ────────────────────────────────────────────────
  const serverOptions: SelectControlOption[] = serverConfigs.map((c) => ({ value: c.id, label: c.name, meta: c.baseUrl }));
  const specOptions: SelectControlOption[] = specs.map((s) => ({ value: s.id, label: s.title }));

  const collectionOptions: SelectControlOption[] = useMemo(() => {
    if (!specDetail) return [];
    return specDetail.groups.flatMap((g) =>
      g.operations
        .filter((o) => o.method.toUpperCase() === 'GET' && isCollectionPath(o.pathTemplate))
        .map((o) => ({ value: o.operationId, label: o.pathTemplate, labelIsPath: true, meta: o.operationId, badge: 'GET', group: g.tag })),
    );
  }, [specDetail]);

  const pickCollection = (operationId: string) => {
    const op = specDetail?.groups.flatMap((g) => g.operations).find((o) => o.operationId === operationId);
    onChange({ ...properties, pickerCollectionOp: operationId, path: op ? stripLeadingSlash(op.pathTemplate) : path });
  };

  // ── Live selection records ──────────────────────────────────────────────────
  const selectionParam: ParameterDefinition = {
    name: 'selection',
    type: 'resourceLocator',
    optionsLoader: 'rest.collection',
    integrationType: 'generic',
    loaderConfig: { path, labelField: labelField || 'name', valueField: valueField || 'id', collectionField },
  };
  const configured = !!(serverConfigId && path);
  const { state: optState, reload } = useNodeOptions({ param: selectionParam, properties, connectionId: serverConfigId, enabled: configured });
  const records = optState.status === 'ready' ? optState.options : [];

  const selectionOptions: SelectControlOption[] = records.map((r) => ({ value: r.value, label: r.label, meta: r.value }));
  const currentSel = readSelection(properties.selection);
  const liveRec = currentSel ? records.find((r) => r.value === currentSel.value) : null;

  const pickSelection = (v: string) => {
    const rec = records.find((r) => r.value === v);
    onChange({ ...properties, selection: { value: v, label: rec?.label ?? v, mode: 'list' } });
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column' }}>
      <p className="rpf-intro" style={{ marginTop: 4 }}>
        Pick a value from a live resource list. The selection is emitted on the node's <code>value</code> /{' '}
        <code>label</code> outputs — drag those into Workflow variables to reuse the choice read-only in other nodes.
      </p>

      <div className="rpf-field">
        <div className="rpf-label">Server Config <span className="rpf-req">*</span></div>
        <SelectControl options={serverOptions} value={serverConfigId} onChange={(v) => set('serverConfigId', v)}
          placeholder="Select server config…" leadingIcon={IconServers} />
      </div>

      <div className="rpf-field">
        <div className="rpf-label">From Imported API</div>
        <SelectControl options={specOptions} value={specId} onChange={(v) => set('pickerSpecId', v)}
          placeholder="Pick the collection path manually" leadingIcon={IconLink} searchable searchPlaceholder="Search APIs…" />
      </div>

      {specId && (
        <div className="rpf-field">
          <div className="rpf-label">Collection <span className="rpf-req">*</span></div>
          <SelectControl options={collectionOptions} value={collectionOp} onChange={pickCollection}
            placeholder="Select a list endpoint…" searchable searchPlaceholder="Search collections…"
            emptyText="No list endpoints in this API." />
        </div>
      )}

      <div className="rpf-field">
        <div className="rpf-label">Collection Path <span className="rpf-req">*</span></div>
        <input className="rpf-input" value={path} placeholder="e.g. pets" spellCheck={false}
          onChange={(e) => set('path', e.target.value)} />
        {specId && <div className="rpf-help">Auto-filled when you pick a collection above; edit to override.</div>}
      </div>

      {/* Advanced — field mapping */}
      <div className={'rpf-disc' + (showAdvanced ? ' rpf-open' : '')}>
        <div className="rpf-disc-head" onClick={() => setShowAdvanced((v) => !v)}>
          <span className="rpf-disc-chev">{IconChevR}</span>
          <span className="rpf-disc-title">Advanced — Field Mapping</span>
          <span className="rpf-disc-tag">2 mapped</span>
        </div>
        {showAdvanced && (
          <div className="rpf-disc-body">
            <p className="rpf-disc-note">
              <strong>Collection field</strong> — dotted path to the array when the response wraps it
              (e.g. <code>pageItems</code>, <code>data.items</code>). Leave blank if the response is the array itself.
            </p>
            <input
              className="rpf-input" style={{ fontSize: '0.82rem', marginBottom: '12px' }}
              value={collectionField} placeholder="blank, or e.g. pageItems"
              onChange={(e) => set('collectionField', e.target.value)}
            />
            <p className="rpf-disc-note">Which property of each item maps to the value &amp; label. Defaults suit most APIs.</p>
            <div className="rpf-map">
              <input className="rpf-map-src" value={valueField} placeholder="id" onChange={(e) => set('valueField', e.target.value)} />
              <span className="rpf-map-arrow">{IconArrow}</span>
              <span className="rpf-map-role">Value</span>
            </div>
            <div className="rpf-map">
              <input className="rpf-map-src" value={labelField} placeholder="name" onChange={(e) => set('labelField', e.target.value)} />
              <span className="rpf-map-arrow">{IconArrow}</span>
              <span className="rpf-map-role">Label</span>
            </div>
          </div>
        )}
      </div>

      <div className="rpf-divider" />

      {/* Selection */}
      <div className="rpf-field">
        <div className="rpf-label">Selection</div>
        {configured ? (
          <>
            <SelectControl
              options={selectionOptions}
              value={currentSel?.value ?? null}
              onChange={pickSelection}
              placeholder="Choose a record…"
              leadingIcon={IconCheckSmall}
              searchable
              searchPlaceholder={records.length ? `Search ${records.length} results…` : 'Search…'}
              loading={optState.status === 'loading'}
              error={optState.status === 'error' ? optState.message : null}
              emptyText="No records found."
              onReload={reload}
            />
            {currentSel && (
              <div className="rpf-preview">
                <div className="rpf-preview-bar">
                  <span className="rpf-pl">Resolved record</span>
                  <span className="rpf-live" style={{ color: liveRec ? '#7fe7d8' : '#44505f' }}>
                    {liveRec ? '● live' : '○ cached'}
                  </span>
                </div>
                <div className="rpf-json">
                  <span className="k">"value"</span>: <span className="s">"{(liveRec ?? currentSel).value}"</span>,{'  '}
                  <span className="k">"label"</span>: <span className="s">"{(liveRec?.label ?? currentSel.label) ?? ''}"</span>
                </div>
              </div>
            )}
          </>
        ) : (
          <p className="rpf-placeholder">Choose a Server Config and a Collection (or path) to load the list.</p>
        )}
      </div>
    </div>
  );
}
