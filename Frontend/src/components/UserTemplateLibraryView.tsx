// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useMemo, useState } from 'react';
import { api, ApiError } from '../utils/api';
import { ensureImportedGroup } from '../utils/importGroup';
import { InstallResult } from './TemplateImporter';
import { CredentialSlotBinding } from './CredentialSlotBinding';
import { TemplateParameterForm, defaultParamValues, paramsSatisfied } from './TemplateParameterForm';
import { TemplatePreview } from './TemplatePreview';
import { TI, categoryVisual } from './templateIcons';
import type { CredentialSummary, GalleryTemplate, ParameterValues, TemplateInstallResponse, TemplatePayloadResponse } from '../types';

type CardState = 'idle' | 'installing' | 'done';

/**
 * The user's saved-template library: the persisted, manageable counterpart to the built-in gallery. Each
 * card installs (binding slots / supplying parameters), previews, or deletes. Saving happens from the
 * Export tab ("Save to library").
 */
export function UserTemplateLibraryView() {
  const [templates, setTemplates] = useState<GalleryTemplate[]>([]);
  const [credentials, setCredentials] = useState<CredentialSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [cardState, setCardState] = useState<Record<string, CardState>>({});
  const [result, setResult] = useState<TemplateInstallResponse | null>(null);

  const [openId, setOpenId] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [bindings, setBindings] = useState<Record<string, string>>({});
  const [paramValues, setParamValues] = useState<ParameterValues>({});
  const [preview, setPreview] = useState<TemplatePayloadResponse | null>(null);
  const [previewing, setPreviewing] = useState(false);

  const load = () => {
    setLoading(true);
    Promise.all([api.listLibraryTemplates(), api.listCredentials()])
      .then(([list, creds]) => { setTemplates(list); setCredentials(creds); })
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load the library.'))
      .finally(() => setLoading(false));
  };

  useEffect(load, []);

  const openConfig = (template: GalleryTemplate) => {
    setOpenId(template.templateId);
    setName(template.manifest.name);
    setBindings({});
    setParamValues(defaultParamValues(template.manifest.parameters));
    setPreview(null);
    setResult(null);
    setError(null);
  };

  const togglePreview = async (template: GalleryTemplate) => {
    if (preview) { setPreview(null); return; }
    setPreviewing(true);
    setError(null);
    try {
      setPreview(await api.getLibraryTemplatePayload(template.templateId, paramValues));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Could not preview this template.');
    } finally {
      setPreviewing(false);
    }
  };

  const create = async (template: GalleryTemplate) => {
    if (cardState[template.templateId] === 'installing') return;
    setError(null);
    setResult(null);
    setCardState((prev) => ({ ...prev, [template.templateId]: 'installing' }));
    try {
      const installed = await api.installLibraryTemplate(template.templateId, bindings, name, paramValues);
      setResult(installed);
      setCardState((prev) => ({ ...prev, [template.templateId]: 'done' }));
      setOpenId(null);
      await ensureImportedGroup(installed.workflowId);
    } catch (err) {
      setCardState((prev) => ({ ...prev, [template.templateId]: 'idle' }));
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Could not create the workflow.');
    }
  };

  const remove = async (template: GalleryTemplate) => {
    setError(null);
    try {
      await api.deleteLibraryTemplate(template.templateId);
      if (openId === template.templateId) setOpenId(null);
      setTemplates((prev) => prev.filter((t) => t.templateId !== template.templateId));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Could not delete the template.');
    }
  };

  const hasTemplates = useMemo(() => templates.length > 0, [templates]);

  return (
    <>
      <div className="phead">
        <h1>Your template library</h1>
        <p>Templates you saved from the Export tab, stored in this instance. Install one as a fresh draft, preview its graph, or remove it.</p>
      </div>

      {error && <div role="alert" className="err-banner">{TI.x()} {error}</div>}
      {result && <InstallResult result={result} />}

      {loading ? (
        <div className="gal-loading"><span className="spin">{TI.spin()}</span> Loading library…</div>
      ) : !hasTemplates ? (
        <div className="gal-empty">No saved templates yet. Export a workflow and choose “Save to library”.</div>
      ) : (
        <div className="gal">
          {templates.map((t) => {
            const { icon, tone } = categoryVisual(t.manifest.category);
            const slots = t.manifest.credentialSlots;
            const params = t.manifest.parameters;
            const state = cardState[t.templateId] ?? 'idle';
            const expanded = openId === t.templateId;
            const canCreate = paramsSatisfied(params, paramValues);
            return (
              <div className={`gcard${state === 'done' ? ' installed' : ''}`} key={t.templateId}>
                <div className="gcard-top">
                  <span className={`gcard-ic ${tone}`}>{TI[icon]()}</span>
                  {t.manifest.category && <span className="gcard-cat">{t.manifest.category}</span>}
                </div>
                <div className="gcard-tt">{t.manifest.name}</div>
                <div className="gcard-by">by <b>{t.manifest.author || 'you'}</b> · v{t.manifest.templateVersion}</div>
                <div className="gcard-desc">{t.manifest.description}</div>

                {expanded && (
                  <div className="gcard-bind">
                    <div className="field" style={{ marginBottom: (slots.length > 0 || params.length > 0) ? 14 : 0 }}>
                      <label htmlFor={`lname-${t.templateId}`}>Workflow name</label>
                      <div className="inp">
                        <input
                          id={`lname-${t.templateId}`}
                          aria-label={`New workflow name for ${t.manifest.name}`}
                          value={name}
                          placeholder={t.manifest.name}
                          onChange={(e) => setName(e.target.value)}
                        />
                      </div>
                    </div>
                    {params.length > 0 && (
                      <div style={{ marginBottom: slots.length > 0 ? 14 : 0 }}>
                        <TemplateParameterForm parameters={params} values={paramValues} onChange={setParamValues} />
                      </div>
                    )}
                    <CredentialSlotBinding slots={slots} credentials={credentials} bindings={bindings} onChange={setBindings} />
                    {preview && (
                      <div style={{ marginTop: 12 }}>
                        <TemplatePreview nodes={preview.nodes} edges={preview.edges} height={200} />
                      </div>
                    )}
                    <div style={{ marginTop: 12 }}>
                      <button className="btn ghost sm" disabled={previewing} onClick={() => togglePreview(t)} aria-label={`Preview ${t.manifest.name}`}>
                        {previewing ? <span className="spin">{TI.spin()}</span> : TI.eye({ width: 14, height: 14 })} {preview ? 'Hide preview' : 'Preview'}
                      </button>
                    </div>
                  </div>
                )}

                <div className="gcard-foot">
                  <div className="gcard-meta">
                    <button className="btn ghost sm" onClick={() => remove(t)} aria-label={`Delete ${t.manifest.name}`}>
                      {TI.trash({ width: 14, height: 14 })} Delete
                    </button>
                  </div>
                  <button
                    className={`btn sm ${state === 'done' ? 'ghost' : 'primary'}`}
                    onClick={() => (expanded ? create(t) : openConfig(t))}
                    disabled={state === 'installing' || (expanded && !canCreate)}
                    aria-label={expanded ? `Create workflow from ${t.manifest.name}` : `Use ${t.manifest.name}`}
                  >
                    {state === 'installing' && <span className="spin">{TI.spin()}</span>}
                    {state === 'done' && TI.check()}
                    {state === 'installing' ? 'Creating…' : state === 'done' ? 'Created' : expanded ? 'Create workflow' : 'Use template'}
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      )}
    </>
  );
}
