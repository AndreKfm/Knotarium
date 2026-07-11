import { useEffect, useRef, useState } from 'react';
import { api, ApiError } from '../utils/api';
import type {
  BundleInstallResponse,
  BundlePackageVerification,
  CredentialSummary,
} from '../types';

// The verification enums serialize as integers (the API has no global string-enum
// converter). Keep the three axes distinct in the UI — a tampered package
// (HashMismatch) is NOT the same as an intact-but-untrusted one.
const SIGNATURE_STATUS = ['No signature', 'Untrusted signature', 'Verified signature'];
const TRUST_LEVEL = ['Untrusted', 'Provisional', 'Verified'];
const VERIFICATION_STATUS = ['Missing', 'Tampered (hash mismatch)', 'Untrusted', 'Provisional', 'Verified'];

const labelOf = (labels: string[], value: number): string => labels[value] ?? `Unknown (${value})`;
const trustColor = (level: number) =>
  level >= 2 ? 'var(--green)' : level === 1 ? 'var(--amber)' : 'var(--rose)';

const Check = () => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3.5" strokeLinecap="round" strokeLinejoin="round"><path d="M20 6 9 17l-5-5" /></svg>
);

function ShieldIcon({ pkg }: { pkg: BundlePackageVerification }) {
  const color = !pkg.hashMatches ? 'var(--rose)' : pkg.trustLevel >= 2 ? 'var(--green)' : pkg.trustLevel === 1 ? 'var(--amber)' : 'var(--rose)';
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke={color} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z" />
    </svg>
  );
}

function StatusBanner({ status, result }: { status: number; result: BundleInstallResponse }) {
  if (status === 200 && result.installed) {
    return (
      <div role="status" className="banner ok">
        <div className="banner-h">✓ Bundle installed</div>
        <ul>
          <li>Installed packages: {result.installedPackages.length ? result.installedPackages.join(', ') : 'none'}</li>
          {result.skippedPackages.length > 0 && <li>Skipped (already present): {result.skippedPackages.join(', ')}</li>}
          <li>
            Imported workflows:{' '}
            {result.importedWorkflows.length
              ? result.importedWorkflows.map((w) => `${w.key} (v${w.versionNumber})`).join(', ')
              : 'none'}
          </li>
          {result.reboundCredentialSlots.length > 0 && (
            <li>Bound credential slots: {result.reboundCredentialSlots.join(', ')}</li>
          )}
        </ul>
        {result.unboundCredentialSlots.length > 0 && (
          <p style={{ color: 'var(--amber)' }}>
            Left unbound: {result.unboundCredentialSlots.join(', ')}. The imported workflows reference these as
            unresolved <code>slot:</code> placeholders — bind them below and re-install, or set the credentials on
            the workflows directly.
          </p>
        )}
      </div>
    );
  }

  if (status === 409) {
    return (
      <div role="alert" className="banner warn">
        <div className="banner-h">⚠ Version conflict — nothing was installed</div>
        <p>
          These packages are already installed at the same version but with different bytes than the bundle pins.
          Installing would silently rebind workflows to the wrong package, so the whole install was rejected.
        </p>
        <ul>{result.conflictingPackages.map((p) => <li key={p}>{p}</li>)}</ul>
      </div>
    );
  }

  // 422 — verification gate rejected the bundle.
  return (
    <div role="alert" className="banner err">
      <div className="banner-h">✕ Verification rejected — nothing was installed</div>
      <p>
        {result.blocking.length} package(s) failed the install gate (see the report below). A rejected install
        writes nothing. Untrusted-but-intact local packages can be installed by opting into provisional installs.
      </p>
    </div>
  );
}

function VerificationReport({ packages }: { packages: BundlePackageVerification[] }) {
  if (packages.length === 0) {
    return (
      <div className="card"><div className="card-body">
        <div className="lh"><h2>Verification report</h2></div>
        <div className="empty">No packages in this bundle — nothing to verify.</div>
      </div></div>
    );
  }
  return (
    <div className="card"><div className="card-body">
      <div className="lh"><h2>Verification report</h2></div>
      <div style={{ overflowX: 'auto' }}>
        <table className="vtable">
          <thead>
            <tr><th>Package</th><th>Trust</th><th>Signature</th><th>Hash</th><th>Status</th><th>Installable</th></tr>
          </thead>
          <tbody>
            {packages.map((p) => (
              <tr key={p.packageId} className={p.installable ? '' : 'bad'}>
                <td><span className="pkgcell"><ShieldIcon pkg={p} /> {p.packageId}</span></td>
                <td style={{ color: trustColor(p.trustLevel), fontWeight: 600 }}>{labelOf(TRUST_LEVEL, p.trustLevel)}</td>
                <td style={{ color: p.signatureStatus === 2 ? 'var(--green)' : 'var(--muted)' }}>{labelOf(SIGNATURE_STATUS, p.signatureStatus)}</td>
                <td style={{ color: p.hashMatches ? 'var(--muted)' : 'var(--rose)', fontWeight: p.hashMatches ? 400 : 700 }}>{p.hashMatches ? 'match' : 'MISMATCH'}</td>
                <td>{labelOf(VERIFICATION_STATUS, p.status)}</td>
                <td style={{ color: p.installable ? 'var(--green)' : 'var(--rose)', fontWeight: 700 }}>{p.installable ? 'yes' : 'no'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div></div>
  );
}

const STEPS = [
  { st: 'Verify signature & lock', ss: 'Hashes and publisher signatures are checked against the bundle lock.', icon: <><path d="M9 12l2 2 4-4" /><path d="M21 12c0 4.97-4.03 9-9 9s-9-4.03-9-9 4.03-9 9-9c1.66 0 3.2.45 4.53 1.23" /></> },
  { st: 'Install packages', ss: 'Resolved registry packages are added at their locked versions.', icon: <><rect x="3" y="3" width="7" height="7" rx="1" /><rect x="14" y="3" width="7" height="7" rx="1" /><rect x="3" y="14" width="7" height="7" rx="1" /><rect x="14" y="14" width="7" height="7" rx="1" /></> },
  { st: 'Import workflows', ss: 'Workflows are imported and credential slots are mapped.', icon: <path d="M3 12h7l2-3 3 6 2-3h4" /> },
  { st: 'Commit atomically', ss: 'Changes apply all-or-nothing once every check passes.', icon: <path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z" />, guard: true },
];

export function BundleInstaller() {
  const [file, setFile] = useState<File | null>(null);
  const [allowProvisional, setAllowProvisional] = useState(false);
  // Explicit acknowledgement of privileged (filesystem/code/database) nodes before installing.
  const [acknowledgePrivileged, setAcknowledgePrivileged] = useState(false);
  const [bindings, setBindings] = useState<Record<string, string>>({});
  const [credentials, setCredentials] = useState<CredentialSummary[]>([]);
  const [result, setResult] = useState<{ status: number; result: BundleInstallResponse } | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dragOver, setDragOver] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    let cancelled = false;
    api.listCredentials()
      .then((creds) => { if (!cancelled) setCredentials(creds); })
      .catch((err) => { console.error('Failed to load credentials:', err); });
    return () => { cancelled = true; };
  }, []);

  const handleInstall = async () => {
    setError(null);
    if (!file) { setError('Please select a .kgbundle file to install.'); return; }
    setLoading(true);
    try {
      const response = await api.installBundle(file, { allowProvisional, credentialBindings: bindings, acknowledgePrivileged });
      setResult(response);
    } catch (err) {
      const message = err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Install failed.';
      setError(message);
      setResult(null);
    } finally {
      setLoading(false);
    }
  };

  const acceptFile = (f: File | null | undefined) => { if (f) { setFile(f); setResult(null); setAcknowledgePrivileged(false); } };
  const handleDrop = (e: React.DragEvent) => { e.preventDefault(); setDragOver(false); acceptFile(e.dataTransfer.files[0]); };

  const sizeLabel = (bytes: number) => {
    const mb = bytes / 1048576;
    return mb < 0.01 ? `${(bytes / 1024).toFixed(0)} KB` : `${mb.toFixed(1)} MB`;
  };

  const requiredSlots = result?.result.requiredCredentialSlots ?? [];

  return (
    <section className="install-pane">
      <div className="phead">
        <h1>Install integration bundle</h1>
        <p>
          Upload a <span className="ext">.kgbundle</span> to verify and install its packages and import its
          workflows. A bundle is fully verified before anything is written — a rejected install leaves the registry
          untouched.
        </p>
      </div>

      <div className="install-grid">
        <div className="install-spacer" aria-hidden="true" />
        <div className="install-main">
          <div
            className={`dropzone${dragOver ? ' drag' : ''}`}
            onClick={() => fileInputRef.current?.click()}
            onDragEnter={(e) => { e.preventDefault(); setDragOver(true); }}
            onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
            onDragLeave={(e) => { e.preventDefault(); setDragOver(false); }}
            onDrop={handleDrop}
          >
            <div className="dz-ic">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M12 3v13" /><path d="m6 9 6-6 6 6" /><path d="M5 21h14" /></svg>
            </div>
            <div className="dz-title"><span className="lk">Click to upload</span> or drag &amp; drop</div>
            <div className="dz-sub">Signed <span className="ext">.kgbundle</span> archive · up to 200 MB</div>
            <input
              ref={fileInputRef}
              type="file"
              accept=".kgbundle,application/zip"
              aria-label="Upload bundle file"
              hidden
              onChange={(e) => acceptFile(e.target.files?.[0])}
            />
          </div>

          {file && (
            <div className="filecard">
              <div className="fic">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" /><path d="m3.3 7 8.7 5 8.7-5" /><path d="M12 22V12" /></svg>
              </div>
              <div>
                <div className="fname">{file.name}</div>
                <div className="fmeta">{sizeLabel(file.size)} · ready to verify</div>
              </div>
              <button className="x" onClick={() => { setFile(null); setResult(null); if (fileInputRef.current) fileInputRef.current.value = ''; }} aria-label="Clear file">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round"><path d="M18 6 6 18M6 6l12 12" /></svg>
              </button>
            </div>
          )}

          <label className={`toggle-row${allowProvisional ? ' sel' : ''}`}>
            <input className="vh" type="checkbox" checked={allowProvisional} onChange={(e) => setAllowProvisional(e.target.checked)} aria-label="Allow provisional installs" />
            <span className="box"><Check /></span>
            <div>
              <div className="tt">Allow provisional installs</div>
              <div className="ts">Permit unsigned, locally authored packages. Use only for packages you trust — these bypass signature verification.</div>
            </div>
          </label>

          {result && result.result.privilegedNodes?.length > 0 && (
            <div className="banner warn" style={{ marginBottom: 12, padding: '12px 14px' }}>
              <div className="banner-h" style={{ marginBottom: 6 }}>⚠ This bundle uses privileged nodes</div>
              <div style={{ fontSize: 12.5, lineHeight: 1.5, opacity: 0.9 }}>
                It can access the host beyond ordinary data flow. Only install bundles from a source you trust.
                Filesystem, database and code capabilities stay off until you enable them in Settings.
                <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
                  {result.result.privilegedNodes.map((p) => (
                    <li key={p.nodeType}><b>{p.displayName}</b> — {p.capabilities.join(', ')}</li>
                  ))}
                </ul>
              </div>
              <label className={`toggle-row${acknowledgePrivileged ? ' sel' : ''}`} style={{ marginTop: 10 }}>
                <input className="vh" type="checkbox" checked={acknowledgePrivileged} onChange={(e) => setAcknowledgePrivileged(e.target.checked)} aria-label="Acknowledge privileged nodes" />
                <span className="box"><Check /></span>
                <div><div className="tt">I understand and want to install this bundle</div></div>
              </label>
            </div>
          )}

          <div className="install-actions">
            <button
              className="btn-primary"
              onClick={handleInstall}
              disabled={loading || !file || (!!result && result.result.privilegedAcknowledgementRequired && !acknowledgePrivileged)}
              aria-label="Install bundle"
            >
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 3v12" /><path d="m7 10 5 5 5-5" /><path d="M5 21h14" /></svg>
              {loading ? 'Installing…' : result && !result.result.installed ? 'Re-install' : 'Install bundle'}
            </button>
          </div>
        </div>

        <div className="card aside-card">
          <div className="card-body">
            <h3>What happens on install</h3>
            <p className="lead">Each stage must pass before the next begins.</p>
            <div className="steps">
              {STEPS.map((step) => (
                <div className={`step${step.guard ? ' guard' : ''}`} key={step.st}>
                  <span className="marker">
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">{step.icon}</svg>
                  </span>
                  <div><div className="st">{step.st}</div><div className="ss">{step.ss}</div></div>
                </div>
              ))}
            </div>
            <div className="safety">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z" /><path d="m9 12 2 2 4-4" /></svg>
              A rejected install leaves your registry exactly as it was.
            </div>
          </div>
        </div>
      </div>

      <div className="install-results">
      {error && <div role="alert" className="banner err" style={{ paddingTop: 14, paddingBottom: 14 }}><div className="banner-h">✕ {error}</div></div>}

      {result && <StatusBanner status={result.status} result={result.result} />}

      {requiredSlots.length > 0 && (
        <div className="card" style={{ marginTop: 16 }}>
          <div className="card-h"><h2>Credential slots</h2></div>
          <div className="card-body">
            <p className="pkg-note">
              Bind each slot to one of your credentials, then re-install. Leaving a slot unbound is allowed — it
              will be reported as unbound and the workflow keeps an unresolved placeholder.
            </p>
            {requiredSlots.map((slot) => (
              <div className="field" key={slot.slot} style={{ marginBottom: 14 }}>
                <label htmlFor={`slot-${slot.slot}`}>
                  {slot.displayName} <span className="hint">({slot.slot} · {slot.type})</span>
                </label>
                {slot.description && <div style={{ fontSize: 11.5, color: 'var(--faint)', marginBottom: 2 }}>{slot.description}</div>}
                <select
                  id={`slot-${slot.slot}`}
                  aria-label={`Bind credential for slot ${slot.slot}`}
                  value={bindings[slot.slot] ?? ''}
                  onChange={(e) => {
                    const value = e.target.value;
                    setBindings((prev) => {
                      const next = { ...prev };
                      if (value) next[slot.slot] = value; else delete next[slot.slot];
                      return next;
                    });
                  }}
                >
                  <option value="">— leave unbound —</option>
                  {credentials.map((c) => <option key={c.id} value={c.id}>{c.name} ({c.id})</option>)}
                </select>
              </div>
            ))}
          </div>
        </div>
      )}

      {result && <VerificationReport packages={result.result.verification} />}
      </div>
    </section>
  );
}
