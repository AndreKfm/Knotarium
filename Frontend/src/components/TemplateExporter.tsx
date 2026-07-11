import { useEffect, useMemo, useState } from 'react';
import { api, ApiError } from '../utils/api';
import { useBlobDownload } from './useBlobDownload';
import { TemplateSelect } from './TemplateSelect';
import { TI } from './templateIcons';
import type { TemplatePortabilizationReport, WorkflowDefinition } from '../types';

interface TemplateExporterProps {
  /** Preselect a workflow (e.g. when deep-linked from the editor's "Export as template"). */
  initialWorkflowId?: string;
}

const slugify = (s: string) => s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '');

export function TemplateExporter({ initialWorkflowId }: TemplateExporterProps) {
  const [workflows, setWorkflows] = useState<WorkflowDefinition[]>([]);
  const [workflowId, setWorkflowId] = useState(initialWorkflowId ?? '');
  const [name, setName] = useState('');
  const [author, setAuthor] = useState('');
  const [description, setDescription] = useState('');
  const [tags, setTags] = useState('');
  const [category, setCategory] = useState('');
  const [version, setVersion] = useState('1.0.0');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [report, setReport] = useState<TemplatePortabilizationReport | null>(null);
  const [doneFile, setDoneFile] = useState<string | null>(null);
  const [savingToLibrary, setSavingToLibrary] = useState(false);
  const [savedToLibrary, setSavedToLibrary] = useState<string | null>(null);
  const download = useBlobDownload();

  useEffect(() => {
    let cancelled = false;
    api.getWorkflows()
      .then((list) => { if (!cancelled) setWorkflows(list); })
      .catch((err) => console.error('Failed to load workflows:', err));
    return () => { cancelled = true; };
  }, []);

  // A stale "Exported" banner shouldn't linger after the form changes.
  useEffect(() => { setDoneFile(null); setReport(null); setSavedToLibrary(null); }, [workflowId, name, author, description, tags, category, version]);

  const selected = useMemo(() => workflows.find((w) => w.id.value === workflowId), [workflows, workflowId]);
  const tname = name.trim() || selected?.name || 'template';
  const fname = `${slugify(tname) || 'template'}-${version || '0.0.0'}.kgtpl`;
  const tagList = tags.split(',').map((t) => t.trim()).filter(Boolean);
  const nodeCount = selected?.nodes?.length ?? 0;

  const handleExport = async () => {
    setError(null);
    if (!workflowId) { setError('Select a workflow to export.'); return; }
    setLoading(true);
    try {
      const result = await api.exportTemplate({
        workflowId,
        name: name.trim() || undefined,
        author: author.trim() || undefined,
        description: description.trim() || undefined,
        tags: tagList,
        category: category.trim() || undefined,
        templateVersion: version.trim() || undefined,
      });
      // Download under the exact name shown in the preview header (derived from name + version),
      // so the saved file matches what the user sees and tweaks before exporting.
      download(result.blob, fname);
      setReport(result.report);
      setDoneFile(fname);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Export failed.');
    } finally {
      setLoading(false);
    }
  };

  const buildRequest = () => ({
    workflowId,
    name: name.trim() || undefined,
    author: author.trim() || undefined,
    description: description.trim() || undefined,
    tags: tagList,
    category: category.trim() || undefined,
    templateVersion: version.trim() || undefined,
  });

  const handleSaveToLibrary = async () => {
    setError(null);
    if (!workflowId) { setError('Select a workflow to save.'); return; }
    setSavingToLibrary(true);
    try {
      const saved = await api.saveTemplateToLibrary(buildRequest());
      setSavedToLibrary(saved.manifest.name);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Save to library failed.');
    } finally {
      setSavingToLibrary(false);
    }
  };

  const reportSlots = report?.slots ?? [];

  return (
    <>
      <div className="phead">
        <h1>Export workflow as template</h1>
        <p>Package any workflow as a portable <span className="kbd">.kgtpl</span>. Credentials are never included — each credential reference becomes a <span className="kbd">slot:</span> placeholder the installer binds to their own.</p>
      </div>

      <div className="exwrap">
        {/* form */}
        <div className="card">
          <div className="card-top">
            <span className="card-ic">{TI.upload({ width: 20, height: 20 })}</span>
            <div>
              <div className="card-tt">Package details</div>
              <div className="card-desc">Metadata shown to whoever installs this template.</div>
            </div>
          </div>
          <div className="card-body">
            <div className="field">
              <label id="tpl-workflow-label">Workflow</label>
              <TemplateSelect
                ariaLabel="Workflow to export"
                placeholder="— select a workflow —"
                value={workflowId}
                options={workflows.map((w) => ({ value: w.id.value, label: `${w.name} (${w.id.value.slice(0, 8)}…)` }))}
                onChange={setWorkflowId}
              />
            </div>
            <div className="field">
              <label htmlFor="tpl-name">Template name <span className="hint">sets the file name</span></label>
              <div className="inp"><input id="tpl-name" value={name} placeholder={selected?.name ?? 'My Template'} onChange={(e) => setName(e.target.value)} /></div>
            </div>
            <div className="field">
              <label htmlFor="tpl-author">Author</label>
              <div className="inp"><input id="tpl-author" value={author} placeholder="Your name" onChange={(e) => setAuthor(e.target.value)} /></div>
            </div>
            <div className="field">
              <label htmlFor="tpl-desc">Description</label>
              <div className="inp"><textarea id="tpl-desc" value={description} placeholder="What does this workflow do? When should someone reach for it?" onChange={(e) => setDescription(e.target.value)} /></div>
            </div>
            <div className="field">
              <label htmlFor="tpl-tags">Tags <span className="hint">comma-separated</span></label>
              <div className="inp"><input id="tpl-tags" value={tags} placeholder="automation, email" onChange={(e) => setTags(e.target.value)} /></div>
            </div>
            <div className="two">
              <div className="field">
                <label htmlFor="tpl-category">Category</label>
                <div className="inp"><input id="tpl-category" value={category} placeholder="uncategorized" onChange={(e) => setCategory(e.target.value)} /></div>
              </div>
              <div className="field">
                <label htmlFor="tpl-version">Version <span className="hint">semver</span></label>
                <div className="inp mono"><input id="tpl-version" value={version} placeholder="1.0.0" onChange={(e) => setVersion(e.target.value)} /></div>
              </div>
            </div>
          </div>
        </div>

        {/* live manifest preview */}
        <div className="mani">
          <div className="mani-head">
            <span className="dots"><i /><i /><i /></span>
            <span className="mf mono" title="Download file name — derived from the template name and version">{fname}</span>
          </div>
          <div className="mani-body mono">
            <div className="mani-row"><span className="mani-k">name</span><span className="mani-s">:</span> <span className="mani-v">"{tname}"</span></div>
            <div className="mani-row"><span className="mani-k">version</span><span className="mani-s">:</span> <span className="mani-v">"{version || '0.0.0'}"</span></div>
            <div className="mani-row"><span className="mani-k">author</span><span className="mani-s">:</span> <span className="mani-v">"{author || '—'}"</span></div>
            <div className="mani-row"><span className="mani-k">category</span><span className="mani-s">:</span> <span className="mani-v">"{category || 'uncategorized'}"</span></div>
            <div className="mani-row" style={{ flexWrap: 'wrap' }}><span className="mani-k">tags</span><span className="mani-s">:</span> <span className="mani-v">[{tagList.map((t) => `"${t}"`).join(', ')}]</span></div>
          </div>
          <div className="mani-stat">
            <div className="ms"><div className="n">{nodeCount || '—'}</div><div className="l">Nodes</div></div>
            <div className="ms"><div className="n" style={{ color: reportSlots.length ? 'var(--amber)' : 'var(--green)' }}>{report ? reportSlots.length : '—'}</div><div className="l">Slots</div></div>
            <div className="ms"><div className="n" style={{ color: 'var(--green)' }}>0</div><div className="l">Secrets</div></div>
          </div>
          <div className="slotbox">
            {!report ? (
              <div className="empty-slot">{TI.shield({ width: 14, height: 14 })} Credential references are stripped into <span className="kbd" style={{ marginLeft: 4 }}>slot:</span> placeholders on export.</div>
            ) : reportSlots.length === 0 ? (
              <>
                <div className="sb-h zero">{TI.shield({ width: 13, height: 13 })} No credentials to strip</div>
                <div className="empty-slot">{TI.check({ width: 14, height: 14 })} This workflow uses no credentials — nothing to slot.</div>
              </>
            ) : (
              <>
                <div className="sb-h">{TI.key({ width: 13, height: 13 })} {reportSlots.length} credential{reportSlots.length > 1 ? 's' : ''} → slots</div>
                {reportSlots.map((s) => (
                  <div className="slot" key={s.slot}>
                    <span className="s-ic">{TI.plug()}</span>
                    <div style={{ minWidth: 0 }}>
                      <div className="s-name">{s.displayName}</div>
                      <div className="s-tok mono slotline">slot:{s.slot}</div>
                    </div>
                  </div>
                ))}
              </>
            )}
          </div>
        </div>
      </div>

      <div className="btn-row">
        <button className="btn ghost" onClick={handleSaveToLibrary} disabled={savingToLibrary || !workflowId} aria-label="Save to library">
          {savingToLibrary ? <span className="spin">{TI.spin()}</span> : TI.grid()} {savingToLibrary ? 'Saving…' : 'Save to library'}
        </button>
        <button className="btn primary" onClick={handleExport} disabled={loading || !workflowId} aria-label="Export template">
          {loading ? <span className="spin">{TI.spin()}</span> : TI.upload()} {loading ? 'Exporting…' : 'Export template'}
        </button>
      </div>

      {savedToLibrary && (
        <div role="status" className="success" style={{ maxWidth: 560, marginLeft: 'auto' }}>
          <span className="sc-ic">{TI.check({ width: 16, height: 16 })}</span>
          <div>
            <div className="sc-t">Saved “{savedToLibrary}” to your library</div>
            <div className="sc-s">Find it under the Library tab to install or share.</div>
          </div>
        </div>
      )}

      {error && <div role="alert" className="err-banner" style={{ maxWidth: 560, marginLeft: 'auto' }}>{TI.x()} {error}</div>}

      {doneFile && (
        <div role="status" className="success" style={{ maxWidth: 560, marginLeft: 'auto' }}>
          <span className="sc-ic">{TI.check({ width: 16, height: 16 })}</span>
          <div>
            <div className="sc-t">Exported <span className="mono">{doneFile}</span></div>
            <div className="sc-s">
              {reportSlots.length === 0
                ? 'No credentials were included. Safe to share.'
                : `${reportSlots.length} credential reference${reportSlots.length > 1 ? 's' : ''} replaced with slot placeholders (${reportSlots.map((s) => `slot:${s.slot}`).join(', ')}). Safe to share.`}
            </div>
          </div>
        </div>
      )}
    </>
  );
}
