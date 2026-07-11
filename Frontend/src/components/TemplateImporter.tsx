import { useEffect, useMemo, useRef, useState } from 'react';
import { api, ApiError } from '../utils/api';
import { ensureImportedGroup } from '../utils/importGroup';
import { CredentialSlotBinding } from './CredentialSlotBinding';
import { TemplateParameterForm, defaultParamValues, paramsSatisfied } from './TemplateParameterForm';
import { TemplatePreview } from './TemplatePreview';
import { TI } from './templateIcons';
import type {
  CredentialSummary,
  ParameterValues,
  TemplateInspectResponse,
  TemplateInstallResponse,
  TemplatePayloadResponse,
} from '../types';

export function TemplateImporter() {
  const [file, setFile] = useState<File | null>(null);
  const [over, setOver] = useState(false);
  const [inspecting, setInspecting] = useState(false);
  const [inspect, setInspect] = useState<TemplateInspectResponse | null>(null);
  const [bindings, setBindings] = useState<Record<string, string>>({});
  const [paramValues, setParamValues] = useState<ParameterValues>({});
  const [workflowName, setWorkflowName] = useState('');
  const [existingNames, setExistingNames] = useState<string[]>([]);
  const [credentials, setCredentials] = useState<CredentialSummary[]>([]);
  const [result, setResult] = useState<TemplateInstallResponse | null>(null);
  const [installing, setInstalling] = useState(false);
  // Explicit acknowledgement gate when the template carries privileged (filesystem/code/database) nodes.
  const [confirmedPrivileged, setConfirmedPrivileged] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<TemplatePayloadResponse | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [savingToLibrary, setSavingToLibrary] = useState(false);
  const [savedToLibrary, setSavedToLibrary] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    let cancelled = false;
    api.listCredentials()
      .then((creds) => { if (!cancelled) setCredentials(creds); })
      .catch((err) => console.error('Failed to load credentials:', err));
    api.getWorkflows()
      .then((list) => { if (!cancelled) setExistingNames(list.map((w) => w.name)); })
      .catch((err) => console.error('Failed to load workflows:', err));
    return () => { cancelled = true; };
  }, []);

  const reset = () => {
    setFile(null); setInspect(null); setBindings({}); setParamValues({}); setResult(null); setError(null);
    setWorkflowName(''); setPreview(null); setSavedToLibrary(null); setConfirmedPrivileged(false);
    if (inputRef.current) inputRef.current.value = '';
  };

  const pick = async (f: File | null | undefined) => {
    if (!f) return;
    setFile(f); setInspect(null); setBindings({}); setParamValues({}); setResult(null); setError(null); setPreview(null); setSavedToLibrary(null); setConfirmedPrivileged(false);
    setInspecting(true);
    try {
      const inspected = await api.inspectTemplate(f);
      setInspect(inspected);
      setParamValues(defaultParamValues(inspected.manifest.parameters)); // prefill from declared defaults
      setWorkflowName(inspected.manifest.name); // prefill, editable
    } catch (err) {
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Could not read this template.');
    } finally {
      setInspecting(false);
    }
  };

  // Live hint: a workflow with this name already exists → the server will save it as "… (2)".
  const nameCollides = useMemo(
    () => workflowName.trim().length > 0 && existingNames.some((n) => n.toLowerCase() === workflowName.trim().toLowerCase()),
    [workflowName, existingNames],
  );

  const handleInstall = async () => {
    if (!file) return;
    setError(null);
    setInstalling(true);
    try {
      const installed = await api.installTemplate(file, bindings, workflowName, paramValues);
      setResult(installed);
      await ensureImportedGroup(installed.workflowId);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Could not create the workflow.');
    } finally {
      setInstalling(false);
    }
  };

  const handleSaveToLibrary = async () => {
    if (!file) return;
    setError(null);
    setSavingToLibrary(true);
    try {
      const saved = await api.saveArchiveToLibrary(file);
      setSavedToLibrary(saved.manifest.name);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Could not save to library.');
    } finally {
      setSavingToLibrary(false);
    }
  };

  const togglePreview = async () => {
    if (preview) { setPreview(null); return; }
    if (!file) return;
    setPreviewing(true);
    setError(null);
    try {
      setPreview(await api.getTemplatePayload(file, paramValues));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Could not preview this template.');
    } finally {
      setPreviewing(false);
    }
  };

  const onDrop = (e: React.DragEvent) => { e.preventDefault(); setOver(false); void pick(e.dataTransfer.files?.[0]); };

  const slots = inspect?.credentialSlots ?? [];
  const parameters = inspect?.manifest.parameters ?? [];
  const allBound = slots.every((s) => bindings[s.slot]);
  const paramsOk = paramsSatisfied(parameters, paramValues);
  const manifest = inspect?.manifest;

  return (
    <>
      <div className="phead">
        <h1>Create a workflow from a template</h1>
        <p>Upload a <span className="kbd">.kgtpl</span> to inspect it, bind any credential slots to your own credentials, and create a new draft workflow from it.</p>
      </div>

      {!file ? (
        <div
          className={`drop${over ? ' over' : ''}`}
          onClick={() => inputRef.current?.click()}
          onDragEnter={(e) => { e.preventDefault(); setOver(true); }}
          onDragOver={(e) => { e.preventDefault(); setOver(true); }}
          onDragLeave={(e) => { e.preventDefault(); setOver(false); }}
          onDrop={onDrop}
        >
          <span className="dz-ic">{TI.upload({ width: 28, height: 28 })}</span>
          <div className="dz-t"><em>Click to upload</em> or drag &amp; drop</div>
          <div className="dz-s">A <span className="kbd">.kgtpl</span> template archive</div>
          <input
            ref={inputRef}
            type="file"
            accept=".kgtpl,application/zip,application/vnd.knotarium.template+zip"
            aria-label="Upload template file"
            style={{ display: 'none' }}
            onChange={(e) => void pick(e.target.files?.[0])}
          />
        </div>
      ) : (
        <div className="filechip">
          <span className="fc-ic">{TI.file()}</span>
          <div style={{ minWidth: 0 }}>
            <div className="fc-n">{file.name}</div>
            <div className="fc-s">{(file.size / 1024).toFixed(0)} KB · {inspecting ? 'inspecting…' : 'template archive'}</div>
          </div>
          <button className="fc-x" onClick={reset} aria-label="Remove file">{TI.x()}</button>
        </div>
      )}

      {inspecting && (
        <div className="callout info">
          <span className="co-ic spin">{TI.spin()}</span>
          <span className="co-t">Reading the manifest and resolving credential slots…</span>
        </div>
      )}

      {error && <div role="alert" className="err-banner">{TI.x()} {error}</div>}

      {manifest && !result && (
        <div className="inspect">
          <div className="in-head">
            <span className="in-ic">{TI.layout({ width: 22, height: 22 })}</span>
            <div>
              <div className="in-tt">{manifest.name}</div>
              <div className="in-by">by {manifest.author || 'unknown'} · v{manifest.templateVersion}</div>
            </div>
            {inspect!.compatibility.supported ? (
              <span className="vok">{TI.check({ width: 12, height: 12 })} Runs here</span>
            ) : (
              <span className="vok warn">{TI.info({ width: 12, height: 12 })} Check compatibility</span>
            )}
          </div>
          <div className="in-grid">
            <div className="in-cell"><div className="in-k">Cred. slots</div><div className="in-v">{slots.length}</div></div>
            <div className="in-cell"><div className="in-k">Compatibility</div><div className="in-v" style={{ fontSize: 13.5 }}>{inspect!.compatibility.supported ? 'Supported' : 'Check'}</div></div>
            <div className="in-cell"><div className="in-k">Installs as</div><div className="in-v" style={{ fontSize: 13.5 }}>Draft</div></div>
          </div>
          <div className="in-body">
            {!inspect!.compatibility.supported && (
              <div className="callout warn" style={{ marginTop: 0, marginBottom: 16 }}>
                <span className="co-ic">{TI.info()}</span>
                <span className="co-t">
                  <b>May not run on this engine.</b> You can still import it as a draft to inspect, but it won't be runnable here.
                  {inspect!.compatibility.warnings.length > 0 && <> {inspect!.compatibility.warnings.join(' ')}</>}
                </span>
              </div>
            )}

            {(inspect!.privilegedNodes ?? []).length > 0 && (
              <div className="callout warn" style={{ marginTop: 0, marginBottom: 16, borderColor: 'rgba(240,180,41,0.4)', background: 'rgba(240,180,41,0.08)' }}>
                <span className="co-ic">{TI.info()}</span>
                <span className="co-t">
                  <b>This template uses privileged nodes.</b> It can access the host beyond ordinary data flow:
                  <ul style={{ margin: '6px 0 8px', paddingLeft: 18 }}>
                    {(inspect!.privilegedNodes ?? []).map((p) => (
                      <li key={p.nodeType} style={{ marginBottom: 2 }}>
                        <b>{p.displayName}</b> — {p.capabilities.join(', ')}
                      </li>
                    ))}
                  </ul>
                  Only install templates from a source you trust. Filesystem, database and code capabilities stay off until you enable them in Settings.
                  <label style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 8, cursor: 'pointer', color: 'var(--text, #e6edf3)' }}>
                    <input type="checkbox" checked={confirmedPrivileged} onChange={(e) => setConfirmedPrivileged(e.target.checked)} style={{ width: 15, height: 15, accentColor: '#f0b429' }} />
                    I understand and want to install this template.
                  </label>
                </span>
              </div>
            )}

            <div className="field" style={{ marginBottom: 18 }}>
              <label htmlFor="tpl-import-name">Workflow name</label>
              <div className="inp">
                <input
                  id="tpl-import-name"
                  aria-label="New workflow name"
                  value={workflowName}
                  placeholder={manifest.name}
                  onChange={(e) => setWorkflowName(e.target.value)}
                />
              </div>
              {nameCollides && (
                <div style={{ fontSize: 12, color: 'var(--amber)', marginTop: 6 }}>
                  A workflow named “{workflowName.trim()}” already exists — this one will be saved as “{workflowName.trim()} (2)”.
                </div>
              )}
            </div>

            {parameters.length > 0 && (
              <div style={{ marginBottom: 18 }}>
                <div className="bind-h">Set parameters</div>
                <div className="bind-sub">
                  This template asks for <b>{parameters.length} value{parameters.length > 1 ? 's' : ''}</b> the author left configurable. They're substituted into the workflow on install.
                </div>
                <TemplateParameterForm parameters={parameters} values={paramValues} onChange={setParamValues} />
              </div>
            )}

            {slots.length > 0 ? (
              <>
                <div className="bind-h">Bind credential slots</div>
                <div className="bind-sub">
                  This template ships with <b>{slots.length} credential slot{slots.length > 1 ? 's' : ''}</b> — placeholders the author left blank on purpose. Point each one at one of your own credentials before installing.
                </div>
                <CredentialSlotBinding slots={slots} credentials={credentials} bindings={bindings} onChange={setBindings} />
              </>
            ) : (
              <div className="empty-slot">{TI.check({ width: 14, height: 14 })} This template needs no credentials — nothing to bind.</div>
            )}

            {preview && (
              <div style={{ marginTop: 16 }}>
                <div className="bind-h" style={{ marginBottom: 8 }}>Preview</div>
                <TemplatePreview nodes={preview.nodes} edges={preview.edges} />
              </div>
            )}

            <div className="btn-row">
              {slots.length > 0 && !allBound && <span style={{ fontSize: 12.5, color: 'var(--faint)' }}>Bind every slot to enable install.</span>}
              {parameters.length > 0 && !paramsOk && <span style={{ fontSize: 12.5, color: 'var(--faint)' }}>Fill every required parameter to enable install.</span>}
              {(inspect!.privilegedNodes ?? []).length > 0 && !confirmedPrivileged && <span style={{ fontSize: 12.5, color: 'var(--amber)' }}>Acknowledge the privileged-node warning to enable install.</span>}
              <button className="btn ghost" disabled={previewing} onClick={togglePreview} aria-label={preview ? 'Hide preview' : 'Preview template'}>
                {previewing ? <span className="spin">{TI.spin()}</span> : TI.eye()} {preview ? 'Hide preview' : 'Preview'}
              </button>
              <button className="btn ghost" disabled={savingToLibrary} onClick={handleSaveToLibrary} aria-label="Save to library">
                {savingToLibrary ? <span className="spin">{TI.spin()}</span> : TI.grid()} {savingToLibrary ? 'Saving…' : 'Save to library'}
              </button>
              <button className="btn primary" disabled={installing || (slots.length > 0 && !allBound) || !paramsOk || ((inspect!.privilegedNodes ?? []).length > 0 && !confirmedPrivileged)} onClick={handleInstall} aria-label="Create workflow">
                {installing ? <span className="spin">{TI.spin()}</span> : TI.download()} {installing ? 'Creating…' : 'Create workflow'}
              </button>
            </div>

            {savedToLibrary && (
              <div role="status" className="success" style={{ marginTop: 14 }}>
                <span className="sc-ic">{TI.check({ width: 16, height: 16 })}</span>
                <div>
                  <div className="sc-t">Saved “{savedToLibrary}” to your library</div>
                  <div className="sc-s">Find it under the Library tab to install or insert later.</div>
                </div>
              </div>
            )}
          </div>
        </div>
      )}

      {result && <InstallResult result={result} />}
    </>
  );
}

export function InstallResult({ result }: { result: TemplateInstallResponse }) {
  return (
    <div role="status" className={`success${result.runnable ? '' : ' warn'}`}>
      <span className="sc-ic">{result.runnable ? TI.check({ width: 16, height: 16 }) : TI.info({ width: 16, height: 16 })}</span>
      <div>
        <div className="sc-t">Imported “{result.workflowName}” as a new draft (v{result.versionNumber})</div>
        <div className="sc-s">
          {result.reboundSlots.length > 0 && <>Bound slots: {result.reboundSlots.join(', ')}. </>}
          {result.configurationRequired
            ? <>Configuration required — bind the remaining slot(s) ({result.openSlots.join(', ')}) on the workflow before publishing or running it.</>
            : result.runnable
              ? <>Nothing runs until you arm and publish it.</>
              : result.diagnostics.length > 0
                ? result.diagnostics.join(' ')
                : <>Imported as a draft.</>}
        </div>
      </div>
    </div>
  );
}
