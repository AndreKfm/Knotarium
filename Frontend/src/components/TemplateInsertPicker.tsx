import { useEffect, useMemo, useRef, useState } from 'react';
import { api, ApiError } from '../utils/api';
import { TemplateParameterForm, defaultParamValues, paramsSatisfied } from './TemplateParameterForm';
import { TemplatePreview } from './TemplatePreview';
import { TI } from './templateIcons';
import type { GalleryTemplate, ParameterValues, TemplatePayloadResponse } from '../types';
import './templates.css';

interface TemplateInsertPickerProps {
  onClose: () => void;
  onInsert: (payload: TemplatePayloadResponse) => void;
}

/** What the user has selected to insert: a gallery/library template (by id) or an uploaded .kgtpl file. */
type Selection =
  | { kind: 'gallery'; template: GalleryTemplate }
  | { kind: 'library'; template: GalleryTemplate }
  | { kind: 'file'; file: File; name: string; parameters: GalleryTemplate['manifest']['parameters'] };

/**
 * Editor overlay for inserting a template's nodes/edges into the open workflow. Lists the built-in gallery
 * and accepts a <c>.kgtpl</c> upload. When the picked template declares parameters, collects their values
 * first (so the inserted graph is already substituted), then fetches the payload and hands it to the canvas.
 * Reskinned onto the shared <c>templates.css</c> design system (embedded variant).
 */
export function TemplateInsertPicker({ onClose, onInsert }: TemplateInsertPickerProps) {
  const [templates, setTemplates] = useState<GalleryTemplate[]>([]);
  const [libraryTemplates, setLibraryTemplates] = useState<GalleryTemplate[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selection, setSelection] = useState<Selection | null>(null);
  const [paramValues, setParamValues] = useState<ParameterValues>({});
  const [preview, setPreview] = useState<TemplatePayloadResponse | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    let cancelled = false;
    Promise.all([api.listGalleryTemplates(), api.listLibraryTemplates()])
      .then(([gallery, library]) => { if (!cancelled) { setTemplates(gallery); setLibraryTemplates(library); } })
      .catch((err) => { if (!cancelled) setError(err instanceof Error ? err.message : 'Failed to load templates.'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, []);

  const parameters = useMemo(() => {
    if (!selection) return [];
    return selection.kind === 'file' ? selection.parameters : selection.template.manifest.parameters;
  }, [selection]);

  const selectCatalog = (kind: 'gallery' | 'library', template: GalleryTemplate) => {
    setError(null);
    setPreview(null);
    setSelection({ kind, template });
    setParamValues(defaultParamValues(template.manifest.parameters));
    if (template.manifest.parameters.length === 0) void doInsert({ kind, template }, {});
  };

  // For an uploaded file we must inspect it first to learn its declared parameters.
  const pickFile = async (file: File | undefined) => {
    if (!file) return;
    setError(null);
    setPreview(null);
    setBusy(true);
    try {
      const inspected = await api.inspectTemplate(file);
      const sel: Selection = { kind: 'file', file, name: inspected.manifest.name, parameters: inspected.manifest.parameters };
      setSelection(sel);
      setParamValues(defaultParamValues(inspected.manifest.parameters));
      if (inspected.manifest.parameters.length === 0) await doInsert(sel, {});
    } catch (err) {
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Could not read that template.');
    } finally {
      setBusy(false);
    }
  };

  const fetchPayload = (sel: Selection, values: ParameterValues) => {
    if (sel.kind === 'gallery') return api.getGalleryTemplatePayload(sel.template.templateId, values);
    if (sel.kind === 'library') return api.getLibraryTemplatePayload(sel.template.templateId, values);
    return api.getTemplatePayload(sel.file, values);
  };

  const doInsert = async (sel: Selection, values: ParameterValues) => {
    setBusy(true);
    setError(null);
    try {
      const payload = await fetchPayload(sel, values);
      onInsert(payload);
      onClose();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Could not read that template.');
      setBusy(false);
    }
  };

  const togglePreview = async () => {
    if (preview) { setPreview(null); return; }
    if (!selection) return;
    setBusy(true);
    setError(null);
    try {
      setPreview(await fetchPayload(selection, paramValues));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Could not preview that template.');
    } finally {
      setBusy(false);
    }
  };

  const selectionName = selection?.kind === 'file' ? selection.name : selection?.template.manifest.name;

  return (
    <div className="tpl-ins-overlay" onClick={onClose} role="dialog" aria-label="Insert template">
      <div className="tpl-ins-panel tpl-screen tpl-embed" onClick={(e) => e.stopPropagation()}>
        <div className="tpl-ins-head">
          <div className="tpl-ins-head-t">Insert from template</div>
          <button className="tpl-ins-x" onClick={onClose} aria-label="Close">{TI.x()}</button>
        </div>

        <div className="tpl-ins-body">
          <p className="tpl-ins-sub">
            Adds the template's nodes onto the current canvas. Credential references arrive as <span className="kbd">slot:</span> placeholders — set them on the nodes before running.
          </p>

          {error && <div role="alert" className="err-banner" style={{ marginBottom: 12 }}>{TI.x()} {error}</div>}

          {selection && parameters.length > 0 ? (
            <>
              <div className="tpl-ins-section">Set parameters for “{selectionName}”</div>
              <TemplateParameterForm parameters={parameters} values={paramValues} onChange={setParamValues} />
              {preview && (
                <div style={{ marginTop: 12 }}>
                  <TemplatePreview nodes={preview.nodes} edges={preview.edges} height={200} />
                </div>
              )}
              <div className="btn-row" style={{ marginTop: 12 }}>
                <button className="btn ghost" onClick={() => { setSelection(null); setPreview(null); }} disabled={busy}>Back</button>
                <button className="btn ghost" onClick={togglePreview} disabled={busy} aria-label={preview ? 'Hide preview' : 'Preview template'}>
                  {TI.eye()} {preview ? 'Hide preview' : 'Preview'}
                </button>
                <button
                  className="btn primary"
                  onClick={() => selection && doInsert(selection, paramValues)}
                  disabled={busy || !paramsSatisfied(parameters, paramValues)}
                  aria-label="Insert template"
                >
                  {busy ? <span className="spin">{TI.spin()}</span> : TI.download()} Insert
                </button>
              </div>
            </>
          ) : (
            <>
              <button
                className="drop"
                style={{ padding: 14, marginBottom: 14 }}
                onClick={() => fileRef.current?.click()}
                disabled={busy}
              >
                <span className="dz-ic">{TI.upload({ width: 22, height: 22 })}</span>
                <div className="dz-t"><em>Upload a .kgtpl file</em></div>
              </button>
              <input
                ref={fileRef}
                type="file"
                accept=".kgtpl,application/zip,application/vnd.knotarium.template+zip"
                aria-label="Upload template file to insert"
                style={{ display: 'none' }}
                onChange={(e) => void pickFile(e.target.files?.[0])}
              />

              {libraryTemplates.length > 0 && (
                <>
                  <div className="tpl-ins-section">From your library</div>
                  {libraryTemplates.map((t) => (
                    <div key={t.templateId} className="tpl-ins-row">
                      <div style={{ minWidth: 0, flex: 1 }}>
                        <div className="tpl-ins-name">{t.manifest.name}</div>
                        <div className="tpl-ins-desc">{t.manifest.description}</div>
                      </div>
                      <button className="btn primary sm" onClick={() => selectCatalog('library', t)} disabled={busy} aria-label={`Insert ${t.manifest.name}`}>
                        {t.manifest.parameters.length > 0 ? 'Configure' : 'Insert'}
                      </button>
                    </div>
                  ))}
                </>
              )}

              <div className="tpl-ins-section">From the gallery</div>
              {loading ? (
                <div className="gal-loading"><span className="spin">{TI.spin()}</span> Loading…</div>
              ) : templates.length === 0 ? (
                <div className="gal-empty">No built-in templates.</div>
              ) : (
                templates.map((t) => (
                  <div key={t.templateId} className="tpl-ins-row">
                    <div style={{ minWidth: 0, flex: 1 }}>
                      <div className="tpl-ins-name">{t.manifest.name}</div>
                      <div className="tpl-ins-desc">{t.manifest.description}</div>
                    </div>
                    <button className="btn primary sm" onClick={() => selectCatalog('gallery', t)} disabled={busy} aria-label={`Insert ${t.manifest.name}`}>
                      {t.manifest.parameters.length > 0 ? 'Configure' : 'Insert'}
                    </button>
                  </div>
                ))
              )}
            </>
          )}
        </div>
      </div>
    </div>
  );
}
