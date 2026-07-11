import { useEffect, useMemo, useRef, useState } from 'react';
import { api, ApiError } from '../utils/api';
import { TI } from './templateIcons';
import type {
  ExternalTargetInfo,
  ImportGranularity,
  ImportInstallResponse,
  ImportPreviewResponse,
  ImportProviderDescriptor,
  ImportProvisionRow,
  ImportReportRow,
  ImportTargetStrategy,
} from '../types';

// Palette (KnotGarden dark/cyan language, per the import-redesign spec).
const C = {
  ink: '#e6edf3', muted: '#6b7888', line: '#18212e', line2: '#131a25',
  panel: 'linear-gradient(180deg,#0d121b,#0b0f17)', chip: '#0c111a', chipBorder: '#232c3a',
  cyan: '#22d3ee', green: '#34d399', amber: '#f0b429', red: '#f0556d',
};
const OUTCOME_COLOR: Record<ImportReportRow['outcome'], string> = { Mapped: C.green, Partial: C.amber, Flagged: C.red };
const ACTION_COLOR: Record<ImportProvisionRow['action'], string> = { Create: C.green, Reuse: C.cyan, Bind: C.cyan, Skip: C.muted };

const STRATEGY_LABEL: Record<ImportTargetStrategy, { title: string; desc: string }> = {
  CreateOrReuse: { title: 'Create automatically', desc: 'Make a connection target per server (reuse any with the same name).' },
  MapToExisting: { title: 'Map to existing', desc: 'Bind each server to a connection you already have.' },
  DontMap: { title: "Don't map", desc: 'Provision nothing; nodes address the default target.' },
};

type FilterKey = 'all' | 'Mapped' | 'Partial' | 'Flagged';

// Colorize bare numbers and "->" inside a construct string (camera/sequence ids → cyan, arrow → →).
function renderConstruct(text: string) {
  const parts = text.replace(/->/g, '→').split(/(\d+)/g);
  return parts.map((p, i) => (/^\d+$/.test(p)
    ? <span key={i} style={{ fontFamily: 'ui-monospace, monospace', color: C.cyan }}>{p}</span>
    : <span key={i}>{p}</span>));
}

function Stepper({ step }: { step: 'upload' | 'configure' | 'report' }) {
  const idx = step === 'upload' ? 0 : step === 'configure' ? 1 : 2;
  const steps = ['Upload file', 'Configure', 'Review coverage'];
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, margin: '18px 0 22px' }}>
      {steps.map((label, i) => {
        const done = i < idx, active = i === idx;
        const color = done ? C.green : active ? C.cyan : C.muted;
        return (
          <div key={label} style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
            <span style={{
              width: 24, height: 24, borderRadius: 999, display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
              fontSize: 12, fontWeight: 700, color: active ? '#06121a' : color,
              background: active ? C.cyan : 'transparent', border: `1.5px solid ${color}`,
            }}>{done ? '✓' : i + 1}</span>
            <span style={{ fontSize: 13.5, fontWeight: active || done ? 600 : 500, color: active || done ? C.ink : C.muted }}>{label}</span>
            {i < 2 && <span style={{ width: 26, height: 1, background: C.line, marginLeft: 4 }} />}
          </div>
        );
      })}
    </div>
  );
}

function SelectCard({ selected, onSelect, icon, title, desc, name, ariaLabel }: {
  selected: boolean; onSelect: () => void; icon: React.ReactNode; title: string; desc: string; name: string; ariaLabel: string;
}) {
  return (
    <label style={{
      flex: 1, display: 'flex', gap: 12, alignItems: 'flex-start', padding: '16px 18px', cursor: 'pointer',
      borderRadius: 14, background: C.panel,
      border: `1px solid ${selected ? C.cyan : C.line}`,
      boxShadow: selected ? `0 0 0 1px ${C.cyan}, 0 0 18px -6px ${C.cyan}` : 'none',
    }}>
      <input type="radio" name={name} aria-label={ariaLabel} checked={selected} onChange={onSelect} style={{ position: 'absolute', opacity: 0, width: 0, height: 0 }} />
      <span style={{
        width: 18, height: 18, marginTop: 2, borderRadius: 999, flexShrink: 0,
        border: `2px solid ${selected ? C.cyan : C.chipBorder}`, display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
      }}>{selected && <span style={{ width: 8, height: 8, borderRadius: 999, background: C.cyan }} />}</span>
      <span style={{ color: selected ? C.cyan : C.muted, marginTop: 1 }}>{icon}</span>
      <span style={{ minWidth: 0 }}>
        <div style={{ fontWeight: 700, fontSize: 14, color: C.ink }}>{title}</div>
        <div style={{ fontSize: 12.5, color: C.muted, marginTop: 3 }}>{desc}</div>
      </span>
    </label>
  );
}

// One button style for the whole import flow: cyan-gradient primary (glow) or dark ghost (optionally danger).
function Btn({ kind = 'ghost', danger = false, disabled, onClick, ariaLabel, children }: {
  kind?: 'primary' | 'ghost'; danger?: boolean; disabled?: boolean; onClick?: () => void; ariaLabel?: string; children: React.ReactNode;
}) {
  const primary = kind === 'primary';
  return (
    <button disabled={disabled} onClick={onClick} aria-label={ariaLabel}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 8, padding: '9px 18px', borderRadius: 10,
        fontSize: 13.5, fontWeight: 600, cursor: disabled ? 'default' : 'pointer', opacity: disabled ? 0.55 : 1, transition: 'filter .12s',
        ...(primary
          ? { background: `linear-gradient(180deg, ${C.cyan}, #0bb6d4)`, color: '#06121a', border: '1px solid transparent', boxShadow: `0 0 20px -6px ${C.cyan}` }
          : { background: C.chip, border: `1px solid ${danger ? 'rgba(240,85,109,0.4)' : C.chipBorder}`, color: danger ? C.red : C.ink }),
      }}>
      {children}
    </button>
  );
}

export function SettingImporter({ onGoToDashboard }: { onGoToDashboard?: () => void } = {}) {
  const [providers, setProviders] = useState<ImportProviderDescriptor[]>([]);
  const [providerId, setProviderId] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [over, setOver] = useState(false);
  const [granularity, setGranularity] = useState<ImportGranularity>('multiple');
  const [strategy, setStrategy] = useState<ImportTargetStrategy>('CreateOrReuse');
  const [serverMap, setServerMap] = useState<Record<string, string>>({});
  const [existingTargets, setExistingTargets] = useState<ExternalTargetInfo[]>([]);
  const [parsed, setParsed] = useState<ImportPreviewResponse | null>(null);
  const [parsing, setParsing] = useState(false);
  const [result, setResult] = useState<ImportInstallResponse | null>(null);
  const [installing, setInstalling] = useState(false);
  const [undoing, setUndoing] = useState(false);
  const [undone, setUndone] = useState(false);
  const [showPreview, setShowPreview] = useState(false); // dry-run report view (no install)
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<FilterKey>('all');
  const [search, setSearch] = useState('');
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    let cancelled = false;
    api.listImportProviders()
      .then((list) => { if (cancelled) return; setProviders(list); if (list.length > 0) setProviderId(list[0].id); })
      .catch((err) => console.error('Failed to load import providers:', err));
    api.getExternalSystem()
      .then((sys) => { if (!cancelled) setExistingTargets(sys.targets); })
      .catch(() => { /* no connection manager configured yet */ });
    return () => { cancelled = true; };
  }, []);

  const provider = providers.find((p) => p.id === providerId) ?? null;
  const accept = provider?.fileExtensions.join(',') ?? '';

  // Preselect the provider's preferred granularity (vendor settings default to "single" — a setting has 100+
  // flows, so one combined workflow beats flooding the dashboard). Only when the provider changes.
  useEffect(() => { if (provider) setGranularity(provider.defaultGranularity); }, [providerId, providers]); // eslint-disable-line react-hooks/exhaustive-deps
  const servers = parsed?.servers ?? [];
  const step: 'upload' | 'configure' | 'report' = (result || showPreview) ? 'report' : file ? 'configure' : 'upload';

  // Re-parse (preview, no side effects) with the current options — runs on select and when the strategy/map
  // changes so the plan + discovered servers stay accurate. Granularity doesn't affect parse output.
  const reparse = async (f: File, strat: ImportTargetStrategy, map: Record<string, string>) => {
    if (!provider) return;
    setError(null); setParsing(true);
    try {
      setParsed(await api.previewImport(provider.id, f, granularity, strat, map));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Could not read this file.');
    } finally {
      setParsing(false);
    }
  };

  const reset = () => {
    setFile(null); setParsed(null); setResult(null); setShowPreview(false); setError(null); setStrategy('CreateOrReuse');
    setServerMap({}); setFilter('all'); setSearch(''); setUndone(false);
    if (inputRef.current) inputRef.current.value = '';
  };

  // Undo the import just made — bulk-archive every workflow it created.
  const doUndo = async () => {
    if (!result) return;
    setUndoing(true); setError(null);
    try {
      await api.bulkDeleteWorkflows(result.installed.map((i) => i.value));
      setUndone(true);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Could not undo the import.');
    } finally {
      setUndoing(false);
    }
  };

  const doPreview = async () => {
    if (!file) return;
    await reparse(file, strategy, serverMap);
    setShowPreview(true);
  };

  const pick = (f: File | null | undefined) => {
    if (!f) return;
    setFile(f); setResult(null); setError(null); setServerMap({});
    void reparse(f, strategy, serverMap);
  };

  const changeStrategy = (s: ImportTargetStrategy) => { setStrategy(s); if (file) void reparse(file, s, serverMap); };
  const changeMap = (alias: string, targetId: string) => {
    const next = { ...serverMap, [alias]: targetId };
    setServerMap(next);
    if (file) void reparse(file, strategy, next);
  };

  const doInstall = async () => {
    if (!file || !provider) return;
    setError(null); setInstalling(true);
    try {
      const r = await api.installImport(provider.id, file, granularity, strategy, serverMap);
      setShowPreview(false);
      setResult(r);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Could not install the workflows.');
    } finally {
      setInstalling(false);
    }
  };

  const onDrop = (e: React.DragEvent) => { e.preventDefault(); setOver(false); pick(e.dataTransfer.files?.[0]); };

  const mappingIncomplete = strategy === 'MapToExisting' && servers.length > 0 && servers.some((s) => !serverMap[s.alias]);

  const report = result?.report ?? parsed?.report ?? [];
  const counts = useMemo(() => ({
    Mapped: report.filter((r) => r.outcome === 'Mapped').length,
    Partial: report.filter((r) => r.outcome === 'Partial').length,
    Flagged: report.filter((r) => r.outcome === 'Flagged').length,
  }), [report]);
  const visibleRows = useMemo(() => report.filter((r) =>
    (filter === 'all' || r.outcome === filter) &&
    (search.trim() === '' || (r.scope + ' ' + r.construct).toLowerCase().includes(search.trim().toLowerCase()))
  ), [report, filter, search]);

  const wide = step === 'report';

  return (
    <div style={{ maxWidth: wide ? 1180 : 880, margin: '0 auto', color: C.ink }}>
      <style>{`
        .imp-row:hover { background: rgba(34,211,238,0.04); }
        .imp-drop:hover { border-color: ${C.cyan}; }
        .imp-seg { color: ${C.muted}; }
        .imp-seg[data-on="1"] { color: #06121a; background: ${C.cyan}; }
        .imp-input::placeholder { color: ${C.muted}; }
      `}</style>

      <div className="phead" style={{ marginBottom: 4 }}>
        <h1 style={{ marginBottom: 6 }}>Import a vendor setting</h1>
        <p style={{ color: C.muted, maxWidth: 640 }}>
          Upload a vendor configuration file to generate KnotGarden workflows. They're created as <b style={{ color: C.ink }}>inactive drafts</b> — review the coverage report, then publish &amp; arm them when you're ready.
        </p>
      </div>

      <Stepper step={step} />

      {error && <div role="alert" className="err-banner" style={{ marginBottom: 14 }}>{TI.x()} {error}</div>}

      {providers.length === 0 ? (
        <div className="callout info"><span className="co-ic">{TI.info()}</span><span className="co-t">No import providers are registered. Install a connection-manager plugin that ships one.</span></div>
      ) : step === 'upload' ? (
        <UploadState
          provider={provider} accept={accept} over={over} setOver={setOver} inputRef={inputRef}
          onPick={pick} onDrop={onDrop}
          showProviderSelect={providers.length > 1} providers={providers} providerId={providerId}
          onProvider={(id) => { setProviderId(id); reset(); }}
        />
      ) : step === 'configure' ? (
        <>
          <FileChip file={file!} provider={provider} parsing={parsing} constructs={parsed?.report.length ?? null} onRemove={reset} />

          <div className="bind-h" style={{ margin: '20px 0 4px' }}>Create as</div>
          <div className="bind-sub" style={{ marginBottom: 12 }}>Land every flow in the file as its own workflow, or merge them all into a single one.</div>
          {provider?.supportsGranularity && (
            <div style={{ display: 'flex', gap: 14 }}>
              <SelectCard selected={granularity === 'single'} onSelect={() => setGranularity('single')} name="granularity" ariaLabel="One combined workflow"
                icon={TI.layout({ width: 18, height: 18 })} title="One combined workflow" desc="All flows in a single workflow as independent chains." />
              <SelectCard selected={granularity === 'multiple'} onSelect={() => setGranularity('multiple')} name="granularity" ariaLabel="Separate workflows"
                icon={TI.grid({ width: 18, height: 18 })} title="Separate workflows" desc="One per flow. Each is armed and managed on its own." />
            </div>
          )}

          {provider?.supportsTargetStrategy && servers.length > 0 && (
            <ConnectionStrategySection
              servers={servers} strategy={strategy} onStrategy={changeStrategy} serverMap={serverMap} onMap={changeMap}
              existingTargets={existingTargets} provisioned={parsed?.provisioned ?? []}
            />
          )}

          <div style={{ display: 'flex', alignItems: 'center', marginTop: 22, gap: 14 }}>
            <span style={{ fontSize: 12.5, color: C.muted, display: 'inline-flex', gap: 6, alignItems: 'center' }}>
              {TI.info({ width: 13, height: 13 })} Imported as inactive drafts — they won't run until you publish.
            </span>
            <div style={{ marginLeft: 'auto', display: 'flex', gap: 10, alignItems: 'center' }}>
              {mappingIncomplete && <span style={{ fontSize: 12.5, color: C.muted }}>Map every server to enable import.</span>}
              <Btn disabled={parsing || !file} onClick={doPreview} ariaLabel="Preview import">
                {parsing ? <span className="spin">{TI.spin()}</span> : TI.eye()} Preview
              </Btn>
              <Btn kind="primary" disabled={installing || mappingIncomplete} onClick={doInstall} ariaLabel="Import workflows">
                {installing ? <span className="spin">{TI.spin()}</span> : TI.download()} {installing ? 'Importing…' : 'Import'}
              </Btn>
            </div>
          </div>
        </>
      ) : (
        // report (preview dry-run OR installed)
        <>
          {result ? (
            <div className="success" role="status" style={{ alignItems: 'flex-start' }}>
              <span className="sc-ic" style={{ color: undone ? C.muted : C.green }}>{TI.check({ width: 18, height: 18 })}</span>
              <div>
                <div className="sc-t" style={{ fontSize: 15 }}>
                  {undone
                    ? <>Undone — removed {result.installed.length} imported workflow{result.installed.length === 1 ? '' : 's'}</>
                    : <>Imported {result.installed.length} workflow{result.installed.length === 1 ? '' : 's'} ({result.granularity === 'single' ? 'combined into one' : 'one per flow'})</>}
                </div>
                <div className="sc-s" style={{ color: C.muted }}>
                  Imported as <b style={{ color: C.ink }}>inactive drafts</b> (no active version) — they won't run until you publish and arm them. Find them on the <b style={{ color: C.ink }}>dashboard</b>.
                </div>
                {result.provisioned.some((p) => p.action === 'Create') && (
                  <div className="sc-s" style={{ color: C.muted, marginTop: 4 }}>
                    Created connections: {result.provisioned.filter((p) => p.action === 'Create').map((p) => p.targetId).join(', ')} — set their passwords in connection settings to bring them online.
                  </div>
                )}
              </div>
            </div>
          ) : (
            <div className="callout info" role="status" style={{ alignItems: 'flex-start' }}>
              <span className="co-ic" style={{ color: C.cyan }}>{TI.eye({ width: 18, height: 18 })}</span>
              <div className="co-t">
                <div style={{ fontWeight: 700, fontSize: 15, color: C.ink }}>
                  Preview — {parsed?.workflows.length ?? 0} workflow{(parsed?.workflows.length ?? 0) === 1 ? '' : 's'} ({granularity === 'single' ? 'combined into one' : 'one per flow'})
                </div>
                <div style={{ color: C.muted, marginTop: 2 }}>Dry run — nothing imported yet. Review the coverage below, then <b style={{ color: C.ink }}>Import</b> to commit.</div>
              </div>
            </div>
          )}

          <div style={{ marginTop: 18, border: `1px solid ${C.line}`, borderRadius: 16, background: C.panel, overflow: 'hidden' }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '14px 16px', flexWrap: 'wrap' }}>
              <div className="bind-h" style={{ margin: 0 }}>Coverage report</div>
              <StatPill color={C.green} label="mapped" n={counts.Mapped} />
              <StatPill color={C.amber} label="partial" n={counts.Partial} />
              <StatPill color={C.red} label="flagged" n={counts.Flagged} />
              <div style={{ marginLeft: 'auto', display: 'flex', gap: 10, alignItems: 'center' }}>
                <div style={{ display: 'flex', background: C.chip, border: `1px solid ${C.chipBorder}`, borderRadius: 10, padding: 2 }}>
                  {(['all', 'Mapped', 'Partial', 'Flagged'] as FilterKey[]).map((k) => (
                    <button key={k} className="imp-seg" data-on={filter === k ? 1 : 0} onClick={() => setFilter(k)}
                      style={{ border: 'none', background: 'transparent', borderRadius: 8, padding: '5px 11px', fontSize: 12.5, fontWeight: 600, cursor: 'pointer' }}>
                      {k === 'all' ? 'All' : k}
                    </button>
                  ))}
                </div>
                <input className="imp-input" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Filter scope or construct"
                  style={{ background: C.chip, border: `1px solid ${C.chipBorder}`, borderRadius: 10, padding: '7px 11px', fontSize: 12.5, color: C.ink, width: 200 }} />
              </div>
            </div>
            <div style={{ maxHeight: 460, overflow: 'auto' }}>
              <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12.5 }}>
                <thead>
                  <tr style={{ position: 'sticky', top: 0, background: '#0b0f17', color: C.muted, textTransform: 'uppercase', fontSize: 11, letterSpacing: 0.4 }}>
                    <th style={{ textAlign: 'left', padding: '9px 16px' }}>Scope</th>
                    <th style={{ textAlign: 'left', padding: '9px 16px' }}>Construct</th>
                    <th style={{ textAlign: 'left', padding: '9px 16px' }}>Outcome</th>
                    <th style={{ textAlign: 'left', padding: '9px 16px' }}>Note</th>
                  </tr>
                </thead>
                <tbody>
                  {visibleRows.map((r, i) => (
                    <tr key={i} className="imp-row" style={{ borderTop: `1px solid ${C.line2}` }}>
                      <td style={{ padding: '9px 16px', whiteSpace: 'nowrap', fontFamily: 'ui-monospace, monospace', color: C.muted }}>{r.scope}</td>
                      <td style={{ padding: '9px 16px' }}>{renderConstruct(r.construct)}</td>
                      <td style={{ padding: '9px 16px' }}>
                        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, color: OUTCOME_COLOR[r.outcome], fontWeight: 600,
                          background: `${OUTCOME_COLOR[r.outcome]}1a`, borderRadius: 999, padding: '3px 10px', fontSize: 12 }}>
                          <span style={{ width: 6, height: 6, borderRadius: 999, background: OUTCOME_COLOR[r.outcome] }} />{r.outcome}
                        </span>
                      </td>
                      <td style={{ padding: '9px 16px', color: C.muted }}>{r.reason ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div style={{ display: 'flex', alignItems: 'center', padding: '12px 16px', borderTop: `1px solid ${C.line2}`, color: C.muted, fontSize: 12.5 }}>
              <span>Showing {visibleRows.length} of {report.length}</span>
              <div style={{ marginLeft: 'auto', display: 'flex', gap: 10 }}>
                {result ? (
                  <>
                    {!undone && (
                      <Btn danger disabled={undoing} onClick={doUndo} ariaLabel="Undo import">
                        {undoing ? <span className="spin">{TI.spin()}</span> : TI.x({ width: 14, height: 14 })} {undoing ? 'Undoing…' : `Undo import (${result.installed.length})`}
                      </Btn>
                    )}
                    <Btn onClick={reset} ariaLabel="Import another">{TI.upload({ width: 14, height: 14 })} Import another</Btn>
                    {onGoToDashboard && <Btn kind="primary" onClick={onGoToDashboard}>{TI.grid({ width: 14, height: 14 })} Go to dashboard</Btn>}
                  </>
                ) : (
                  <>
                    <Btn onClick={() => setShowPreview(false)} ariaLabel="Back to configure">{TI.x({ width: 14, height: 14 })} Back</Btn>
                    <Btn kind="primary" disabled={installing || mappingIncomplete} onClick={doInstall} ariaLabel="Import workflows">
                      {installing ? <span className="spin">{TI.spin()}</span> : TI.download()} {installing ? 'Importing…' : 'Import'}
                    </Btn>
                  </>
                )}
              </div>
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function StatPill({ color, label, n }: { color: string; label: string; n: number }) {
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, color, background: `${color}1a`, borderRadius: 999, padding: '4px 11px', fontSize: 12.5, fontWeight: 600 }}>
      <span style={{ width: 6, height: 6, borderRadius: 999, background: color }} />{n} {label}
    </span>
  );
}

function UploadState({ provider, accept, over, setOver, inputRef, onPick, onDrop, showProviderSelect, providers, providerId, onProvider }: {
  provider: ImportProviderDescriptor | null; accept: string; over: boolean; setOver: (v: boolean) => void;
  inputRef: React.RefObject<HTMLInputElement | null>; onPick: (f: File | null | undefined) => void; onDrop: (e: React.DragEvent) => void;
  showProviderSelect: boolean; providers: ImportProviderDescriptor[]; providerId: string; onProvider: (id: string) => void;
}) {
  return (
    <>
      {showProviderSelect && (
        <div className="field" style={{ marginBottom: 14 }}>
          <label htmlFor="imp-provider">Source format</label>
          <div className="inp"><select id="imp-provider" value={providerId} onChange={(e) => onProvider(e.target.value)}>
            {providers.map((p) => <option key={p.id} value={p.id}>{p.displayName}</option>)}
          </select></div>
        </div>
      )}
      <div className="imp-drop" onClick={() => inputRef.current?.click()}
        onDragEnter={(e) => { e.preventDefault(); setOver(true); }} onDragOver={(e) => { e.preventDefault(); setOver(true); }}
        onDragLeave={(e) => { e.preventDefault(); setOver(false); }} onDrop={onDrop}
        style={{
          border: `1.5px dashed ${over ? C.cyan : C.chipBorder}`, borderRadius: 16, padding: '54px 24px', textAlign: 'center', cursor: 'pointer',
          background: over ? 'rgba(34,211,238,0.05)' : 'transparent', transition: 'all .15s',
        }}>
        <span style={{ display: 'inline-flex', width: 64, height: 64, borderRadius: 16, alignItems: 'center', justifyContent: 'center',
          background: 'rgba(34,211,238,0.08)', color: C.cyan, marginBottom: 16 }}>{TI.upload({ width: 28, height: 28 })}</span>
        <div style={{ fontSize: 18, fontWeight: 600 }}><span style={{ color: C.cyan }}>Click to upload</span> or drag &amp; drop</div>
        <div style={{ color: C.muted, marginTop: 6 }}>Drop your vendor configuration here to begin</div>
        <span style={{ display: 'inline-flex', gap: 6, marginTop: 16, padding: '7px 14px', borderRadius: 999, background: C.chip, border: `1px solid ${C.chipBorder}`, fontSize: 12.5, color: C.muted }}>
          Accepts <span style={{ fontFamily: 'ui-monospace, monospace', color: C.cyan }}>{accept || '.set'}</span> · {provider?.displayName ?? 'vendor setting'}
        </span>
        <input ref={inputRef} type="file" accept={accept} aria-label="Upload setting file" style={{ display: 'none' }} onChange={(e) => onPick(e.target.files?.[0])} />
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3,1fr)', gap: 14, marginTop: 16 }}>
        {[
          { ic: TI.upload({ width: 16, height: 16 }), n: 'STEP 1', t: 'Upload & translate', d: 'We parse the vendor file and map every event and action onto KnotGarden constructs.' },
          { ic: TI.eye({ width: 16, height: 16 }), n: 'STEP 2', t: 'Review coverage', d: 'Inspect what mapped cleanly, what came in partial, and which constructs got flagged for attention.' },
          { ic: TI.check({ width: 16, height: 16 }), n: 'STEP 3', t: 'Publish & arm', d: 'Workflows land as inactive drafts — nothing runs until you publish and arm each one.' },
        ].map((s) => (
          <div key={s.n} style={{ border: `1px solid ${C.line}`, borderRadius: 14, background: C.panel, padding: '16px 18px' }}>
            <div style={{ display: 'flex', alignItems: 'center' }}>
              <span style={{ width: 30, height: 30, borderRadius: 9, background: 'rgba(34,211,238,0.08)', color: C.cyan, display: 'inline-flex', alignItems: 'center', justifyContent: 'center' }}>{s.ic}</span>
              <span style={{ marginLeft: 'auto', fontSize: 10.5, letterSpacing: 0.6, color: C.muted, fontWeight: 700 }}>{s.n}</span>
            </div>
            <div style={{ fontWeight: 700, marginTop: 12 }}>{s.t}</div>
            <div style={{ fontSize: 12.5, color: C.muted, marginTop: 5 }}>{s.d}</div>
          </div>
        ))}
      </div>
    </>
  );
}

function FileChip({ file, provider, parsing, constructs, onRemove }: {
  file: File; provider: ImportProviderDescriptor | null; parsing: boolean; constructs: number | null; onRemove: () => void;
}) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 14, padding: '16px 18px', border: `1px solid ${C.line}`, borderRadius: 14, background: C.panel }}>
      <span style={{ width: 40, height: 40, borderRadius: 11, background: 'rgba(34,211,238,0.08)', color: C.cyan, display: 'inline-flex', alignItems: 'center', justifyContent: 'center' }}>{TI.file({ width: 18, height: 18 })}</span>
      <div style={{ minWidth: 0 }}>
        <div style={{ fontWeight: 700 }}>{file.name}</div>
        <div style={{ fontFamily: 'ui-monospace, monospace', fontSize: 12, color: C.muted, marginTop: 3 }}>
          {(file.size / 1024).toFixed(0)} KB · {provider?.displayName}{constructs != null ? ` · ${constructs} constructs detected` : ''}
        </div>
      </div>
      <span style={{ marginLeft: 'auto', display: 'inline-flex', alignItems: 'center', gap: 6, color: parsing ? C.muted : C.green, background: parsing ? 'transparent' : `${C.green}1a`, borderRadius: 999, padding: '5px 12px', fontSize: 12.5, fontWeight: 600 }}>
        {parsing ? <><span className="spin">{TI.spin()}</span> Parsing…</> : <>{TI.check({ width: 13, height: 13 })} Parsed</>}
      </span>
      <button className="fc-x" onClick={onRemove} aria-label="Remove file" style={{ marginLeft: 4 }}>{TI.x()}</button>
    </div>
  );
}

function ConnectionStrategySection({ servers, strategy, onStrategy, serverMap, onMap, existingTargets, provisioned }: {
  servers: ImportPreviewResponse['servers']; strategy: ImportTargetStrategy; onStrategy: (s: ImportTargetStrategy) => void;
  serverMap: Record<string, string>; onMap: (alias: string, id: string) => void; existingTargets: ExternalTargetInfo[]; provisioned: ImportProvisionRow[];
}) {
  return (
    <div style={{ marginTop: 22 }}>
      <div className="bind-h" style={{ marginBottom: 4 }}>Device connections ({servers.length})</div>
      <div className="bind-sub" style={{ marginBottom: 12 }}>
        This setting drives {servers.length} server{servers.length === 1 ? '' : 's'} ({servers.map((s) => s.alias).join(', ')}). Choose how to connect them.
      </div>
      <div style={{ display: 'flex', gap: 14 }}>
        {(['CreateOrReuse', 'MapToExisting', 'DontMap'] as ImportTargetStrategy[]).map((s) => (
          <SelectCard key={s} selected={strategy === s} onSelect={() => onStrategy(s)} name="strategy" ariaLabel={STRATEGY_LABEL[s].title}
            icon={TI.layout({ width: 18, height: 18 })} title={STRATEGY_LABEL[s].title} desc={STRATEGY_LABEL[s].desc} />
        ))}
      </div>

      {strategy === 'MapToExisting' && (
        <div style={{ marginTop: 12 }}>
          {servers.map((s) => (
            <div key={s.alias} style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 8 }}>
              <span style={{ minWidth: 200, fontSize: 12.5, fontFamily: 'ui-monospace, monospace' }}>{s.alias}{s.host ? ` (${s.host})` : ''}</span>
              <span style={{ color: C.muted }}>→</span>
              <select value={serverMap[s.alias] ?? ''} onChange={(e) => onMap(s.alias, e.target.value)}
                style={{ background: C.chip, border: `1px solid ${C.chipBorder}`, borderRadius: 9, padding: '6px 10px', color: C.ink, fontSize: 12.5 }}>
                <option value="">— pick a target —</option>
                {existingTargets.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
              </select>
            </div>
          ))}
          {existingTargets.length === 0 && <div style={{ fontSize: 12, color: C.amber }}>No existing targets to map to — create some in connection settings, or pick another option.</div>}
        </div>
      )}

      {provisioned.length > 0 && strategy !== 'DontMap' && (
        <div style={{ marginTop: 12, fontSize: 12.5, display: 'flex', flexWrap: 'wrap', gap: 12, alignItems: 'center' }}>
          <span style={{ color: C.muted }}>Plan:</span>
          {provisioned.map((p) => (
            <span key={p.serverAlias}>
              {p.serverAlias} <span style={{ color: ACTION_COLOR[p.action], fontWeight: 600 }}>{p.action}</span>{p.targetId ? ` → ${p.targetId}` : ''}
            </span>
          ))}
          <span style={{ color: C.muted }}>(created targets need a password set in connection settings)</span>
        </div>
      )}
    </div>
  );
}
