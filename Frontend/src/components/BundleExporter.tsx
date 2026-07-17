// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useMemo, useState } from 'react';
import { api, ApiError } from '../utils/api';
import type {
  BundleManifestInput,
  BundleCredentialSlot,
  NodePackageSummary,
  WorkflowDefinition,
} from '../types';

interface SlotDraft {
  key: string;
  label: string;
}

// Built-in packages ship with the engine, carry no installable registry version
// (their source is "Built-in …"), and can't be bundled. A package is bundleable
// only if it has at least one real (non-built-in) registry version.
const bundleableVersion = (pkg: NodePackageSummary) =>
  pkg.versions.find((v) => !v.source.startsWith('Built-in'));

function downloadBlob(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

const Check = () => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3.5" strokeLinecap="round" strokeLinejoin="round"><path d="M20 6 9 17l-5-5" /></svg>
);

export function BundleExporter() {
  // Metadata
  const [bundleId, setBundleId] = useState('');
  const [name, setName] = useState('');
  const [version, setVersion] = useState('1.0.0');
  const [publisher, setPublisher] = useState('');
  const [tags, setTags] = useState('');
  const [category, setCategory] = useState('');

  // Selections
  const [workflows, setWorkflows] = useState<WorkflowDefinition[]>([]);
  const [packages, setPackages] = useState<NodePackageSummary[]>([]);
  const [selectedWorkflows, setSelectedWorkflows] = useState<Set<string>>(new Set());
  const [slots, setSlots] = useState<SlotDraft[]>([]);

  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    Promise.all([api.getWorkflows(), api.getNodePackages()])
      .then(([wf, pkg]) => {
        if (cancelled) return;
        setWorkflows(wf);
        setPackages(pkg);
      })
      .catch((err) => { console.error('Failed to load workflows/packages:', err); });
    return () => { cancelled = true; };
  }, []);

  const toggleWorkflow = (id: string) => {
    setSelectedWorkflows((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const allSelected = workflows.length > 0 && selectedWorkflows.size === workflows.length;
  const toggleSelectAll = () =>
    setSelectedWorkflows(allSelected ? new Set() : new Set(workflows.map((w) => w.id.value)));

  // Packages are derived, not picked: every node's `type` is a package id, so the
  // bundle's packages are exactly the bundleable (non-built-in) packages used by the
  // selected workflows. Built-in node types (errorTrigger, …) drop out automatically.
  const derivedPackages = useMemo(() => {
    const usedTypes = new Set<string>();
    workflows
      .filter((w) => selectedWorkflows.has(w.id.value))
      .forEach((w) => w.nodes.forEach((n) => usedTypes.add(n.type)));

    return packages
      .filter((p) => usedTypes.has(p.id))
      .map((p) => ({ pkg: p, version: bundleableVersion(p) }))
      .filter((x): x is { pkg: NodePackageSummary; version: NonNullable<ReturnType<typeof bundleableVersion>> } => x.version != null)
      .map(({ pkg, version }) => ({ id: pkg.id, version: version.version, source: version.source }));
  }, [workflows, selectedWorkflows, packages]);

  const declaredSlots = slots.filter((slot) => slot.key.trim().length > 0).length;

  const buildManifest = (): BundleManifestInput => {
    const wfRefs = workflows
      .filter((w) => selectedWorkflows.has(w.id.value))
      .map((w) => ({ key: w.id.value, role: 'primary', ref: `${w.id.value}.json` }));

    const pkgRefs = derivedPackages.map((p) => ({
      id: p.id,
      versionConstraintOrPin: p.version,
      source: p.source,
    }));

    const credentialSlots: BundleCredentialSlot[] = slots
      .filter((slot) => slot.key.trim().length > 0)
      .map((slot) => ({
        slot: slot.key.trim(),
        type: '',
        displayName: slot.label.trim() || slot.key.trim(),
        description: null,
        checklist: [],
      }));

    return {
      bundleId: bundleId.trim(),
      bundleVersion: version.trim() || '0.0.0',
      name: name.trim() || bundleId.trim(),
      publisher: publisher.trim(),
      tags: tags.split(',').map((t) => t.trim()).filter(Boolean),
      category: category.trim(),
      schemaVersion: 1,
      minEngineVersion: '0.9.0',
      packages: pkgRefs,
      credentialSlots,
      workflows: wfRefs,
      provenance: { source: 'local', publisher: publisher.trim() },
    };
  };

  const handleExport = async () => {
    setError(null);
    setDone(null);
    if (!bundleId.trim()) { setError('A bundle id is required.'); return; }
    if (selectedWorkflows.size === 0) { setError('Select at least one workflow to bundle.'); return; }
    const manifest = buildManifest();
    setLoading(true);
    try {
      const blob = await api.exportBundle(manifest);
      downloadBlob(blob, `${manifest.bundleId}-${manifest.bundleVersion}.kgbundle`);
      setDone(`${manifest.bundleId}-${manifest.bundleVersion}.kgbundle`);
    } catch (err) {
      const message = err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Export failed.';
      setError(message);
    } finally {
      setLoading(false);
    }
  };

  const addSlot = () => setSlots((prev) => [...prev, { key: '', label: '' }]);
  const updateSlot = (i: number, patch: Partial<SlotDraft>) =>
    setSlots((prev) => prev.map((slot, idx) => (idx === i ? { ...slot, ...patch } : slot)));
  const removeSlot = (i: number) => setSlots((prev) => prev.filter((_, idx) => idx !== i));

  return (
    <section className="wrap" style={{ paddingTop: 0 }}>
      <div className="phead">
        <h1>Export integration bundle</h1>
        <p>
          Assemble a <span className="ext">.kgbundle</span> from your workflows and installed packages. The server
          resolves package versions, hashes and signs them into a lock — only installed registry packages can be
          bundled (built-in nodes can't).
        </p>
        <p>
          Workflows are exported as-is. For a portable bundle, included workflows should reference credentials as{' '}
          <code>slot:&lt;name&gt;</code> placeholders matching the slots declared below — export does not rewrite
          real credential ids.
        </p>
      </div>

      {/* metadata */}
      <div className="card">
        <div className="card-h"><h2>Metadata</h2></div>
        <div className="card-body">
          <div className="grid2">
            <div className="field">
              <label>Bundle id <span className="req">*</span></label>
              <input className="mono" value={bundleId} onChange={(e) => setBundleId(e.target.value)} placeholder="com.example.integration" aria-label="Bundle id" />
            </div>
            <div className="field">
              <label>Version</label>
              <input className="mono" value={version} onChange={(e) => setVersion(e.target.value)} placeholder="1.0.0" aria-label="Bundle version" />
            </div>
            <div className="field">
              <label>Name</label>
              <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Example Integration" aria-label="Bundle name" />
            </div>
            <div className="field">
              <label>Publisher</label>
              <input value={publisher} onChange={(e) => setPublisher(e.target.value)} placeholder="Example" aria-label="Publisher" />
            </div>
            <div className="field">
              <label>Category</label>
              <input value={category} onChange={(e) => setCategory(e.target.value)} placeholder="Communication" aria-label="Category" />
            </div>
            <div className="field">
              <label>Tags <span className="hint">comma-separated</span></label>
              <input value={tags} onChange={(e) => setTags(e.target.value)} placeholder="messaging, example" aria-label="Tags" />
            </div>
          </div>
        </div>
      </div>

      {/* workflows */}
      <div className="card">
        <div className="card-body">
          <div className="lh">
            <h2>Workflows</h2>
            <span className="spacer" />
            {workflows.length > 0 && (
              <button className="linkbtn" onClick={toggleSelectAll} aria-label={allSelected ? 'Clear all workflows' : 'Select all workflows'}>
                {allSelected ? 'Clear all' : 'Select all'}
              </button>
            )}
          </div>
          <div className="wf-list">
            {workflows.length === 0 ? (
              <div className="empty">No workflows available.</div>
            ) : (
              workflows.map((w) => {
                const sel = selectedWorkflows.has(w.id.value);
                return (
                  <label key={w.id.value} className={`wf-item${sel ? ' sel' : ''}`}>
                    <input className="vh" type="checkbox" checked={sel} onChange={() => toggleWorkflow(w.id.value)} aria-label={`Include workflow ${w.name}`} />
                    <span className="box"><Check /></span>
                    <span className="name">{w.name}</span>
                    <span className="id">{w.id.value}</span>
                  </label>
                );
              })
            )}
          </div>
        </div>
      </div>

      {/* packages (auto-detected) */}
      <div className="card">
        <div className="card-body">
          <div className="lh"><h2>Packages <span className="hsub">auto-detected</span></h2></div>
          <p className="pkg-note">
            The packages your selected workflows use, pinned to their latest installed version. Built-in nodes ship
            with the engine and aren't bundled.
          </p>
          <div className="pkg-list">
            {selectedWorkflows.size === 0 ? (
              <div className="pkg-empty">Select workflows above to see the packages they bring in.</div>
            ) : derivedPackages.length === 0 ? (
              <div className="pkg-empty">The selected workflows use only built-in nodes — nothing to bundle.</div>
            ) : (
              derivedPackages.map((p) => (
                <div className="pkg-row" key={p.id}>
                  <span className="pdot" />
                  <span className="pname">{p.id}</span>
                  <span className="pver">@ {p.version}</span>
                </div>
              ))
            )}
          </div>
        </div>
      </div>

      {/* credential slots */}
      <div className="card">
        <div className="card-h"><h2>Credential slots</h2></div>
        <div className="card-body">
          <p style={{ margin: '0 0 10px', fontSize: 12.5, color: 'var(--faint)', lineHeight: 1.5 }}>
            Declare the <span className="slot-mono">slot:&lt;name&gt;</span> placeholders this bundle expects.
            Installers map each slot to one of their own credentials.
          </p>
          <div>
            {slots.map((slot, i) => (
              <div className="slot" key={i}>
                <input className="key" value={slot.key} onChange={(e) => updateSlot(i, { key: e.target.value })} placeholder="slot_name" aria-label={`Slot name ${i}`} />
                <input className="lbl" value={slot.label} onChange={(e) => updateSlot(i, { label: e.target.value })} placeholder="Human-readable label" aria-label={`Slot label ${i}`} />
                <button className="rm" onClick={() => removeSlot(i)} aria-label={`Remove slot ${i}`}>
                  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M18 6 6 18M6 6l12 12" /></svg>
                </button>
              </div>
            ))}
          </div>
          <button className="addslot" onClick={addSlot} aria-label="Add credential slot">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M12 5v14M5 12h14" /></svg>
            Add credential slot
          </button>
        </div>
      </div>

      {error && <div role="alert" className="banner err" style={{ paddingTop: 14, paddingBottom: 14 }}><div className="banner-h">✕ {error}</div></div>}
      {done && <div role="status" className="banner ok" style={{ paddingTop: 14, paddingBottom: 14 }}><div className="banner-h">✓ Exported {done}</div></div>}

      {/* sticky action bar */}
      <div className="actionbar">
        <div className="summary">
          <b>{selectedWorkflows.size}</b> workflow{selectedWorkflows.size === 1 ? '' : 's'} <span className="sep">·</span>{' '}
          <b>{derivedPackages.length}</b> package{derivedPackages.length === 1 ? '' : 's'} <span className="sep">·</span>{' '}
          <b>{declaredSlots}</b> slot{declaredSlots === 1 ? '' : 's'}
        </div>
        <span className="spacer" />
        <button className="btn-primary" onClick={handleExport} disabled={loading} aria-label="Export bundle">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 3v12" /><path d="m7 8 5-5 5 5" /><path d="M5 21h14" /></svg>
          {loading ? 'Exporting…' : 'Export .kgbundle'}
        </button>
      </div>
    </section>
  );
}
