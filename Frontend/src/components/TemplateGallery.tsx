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

interface TemplateGalleryProps {
  /** When provided, a freshly created workflow opens straight in the editor instead of just banner-confirming. */
  onOpenWorkflow?: (workflowId: string) => void;
}

export function TemplateGallery({ onOpenWorkflow }: TemplateGalleryProps = {}) {
  const [templates, setTemplates] = useState<GalleryTemplate[]>([]);
  const [credentials, setCredentials] = useState<CredentialSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [category, setCategory] = useState('All');
  const [cardState, setCardState] = useState<Record<string, CardState>>({});
  const [result, setResult] = useState<TemplateInstallResponse | null>(null);

  // Per-card "Use template" config: which card is expanded, plus its name + slot bindings + params.
  const [openId, setOpenId] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [bindings, setBindings] = useState<Record<string, string>>({});
  const [paramValues, setParamValues] = useState<ParameterValues>({});
  const [preview, setPreview] = useState<TemplatePayloadResponse | null>(null);
  const [previewing, setPreviewing] = useState(false);

  useEffect(() => {
    let cancelled = false;
    Promise.all([api.listGalleryTemplates(), api.listCredentials()])
      .then(([list, creds]) => { if (!cancelled) { setTemplates(list); setCredentials(creds); } })
      .catch((err) => { if (!cancelled) setError(err instanceof Error ? err.message : 'Failed to load gallery.'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, []);

  const categories = useMemo(() => {
    const distinct = [...new Set(templates.map((t) => t.manifest.category).filter(Boolean))];
    return ['All', ...distinct];
  }, [templates]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return templates.filter((t) => {
      const m = t.manifest;
      const okCat = category === 'All' || (m.category || '').toLowerCase() === category.toLowerCase();
      const okQ = !q || `${m.name} ${m.description} ${m.tags.join(' ')}`.toLowerCase().includes(q);
      return okCat && okQ;
    });
  }, [templates, query, category]);

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
      setPreview(await api.getGalleryTemplatePayload(template.templateId, paramValues));
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
      const installed = await api.installGalleryTemplate(template.templateId, bindings, name, paramValues);
      setResult(installed);
      setCardState((prev) => ({ ...prev, [template.templateId]: 'done' }));
      setOpenId(null);
      await ensureImportedGroup(installed.workflowId);
      // Jump straight into the new workflow so the user lands on their canvas, not a confirmation
      // banner. The banner (InstallResult) stays as the fallback when no navigation handler is wired.
      onOpenWorkflow?.(installed.workflowId);
    } catch (err) {
      setCardState((prev) => ({ ...prev, [template.templateId]: 'idle' }));
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Could not create the workflow.');
    }
  };

  return (
    <>
      <div className="phead">
        <h1>Template gallery</h1>
        <p>Create a new workflow from a starter to learn the editor or use as a scaffold. Each one is added as a fresh draft you can run right away.</p>
      </div>

      <div className="gal-controls">
        <div className="search">
          <span className="si">{TI.search()}</span>
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search templates, tags…"
            aria-label="Search templates"
          />
        </div>
        <div className="chips">
          {categories.map((c) => (
            <button key={c} className={`chip${c === category ? ' on' : ''}`} onClick={() => setCategory(c)}>{c}</button>
          ))}
        </div>
      </div>

      {error && <div role="alert" className="err-banner">{TI.x()} {error}</div>}
      {result && <InstallResult result={result} />}

      {loading ? (
        <div className="gal-loading"><span className="spin">{TI.spin()}</span> Loading gallery…</div>
      ) : filtered.length === 0 ? (
        <div className="gal-empty">{query ? `No templates match “${query}”.` : 'No built-in templates are available.'}</div>
      ) : (
        <div className="gal">
          {filtered.map((t) => {
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
                <div className="gcard-by">by <b>{t.manifest.author || 'Knotarium'}</b> · v{t.manifest.templateVersion}</div>
                <div className="gcard-desc">{t.manifest.description}</div>
                {t.manifest.tags.length > 0 && (
                  <div className="gcard-tags">
                    {t.manifest.tags.map((tag) => <span key={tag} className="gtag">{tag}</span>)}
                  </div>
                )}

                {expanded && (
                  <div className="gcard-bind">
                    <div className="field" style={{ marginBottom: (slots.length > 0 || params.length > 0) ? 14 : 0 }}>
                      <label htmlFor={`gname-${t.templateId}`}>Workflow name</label>
                      <div className="inp">
                        <input
                          id={`gname-${t.templateId}`}
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
                    <span className={`slotn${slots.length === 0 ? ' zero' : ''}`}>
                      {TI.key()} {slots.length === 0 ? 'no slots' : `${slots.length} slot${slots.length > 1 ? 's' : ''}`}
                    </span>
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
