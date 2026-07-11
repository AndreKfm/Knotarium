import { useRef, useState } from 'react';
import {
  Database,
  Download,
  Upload,
  Shield,
  ShieldAlert,
  AlertTriangle,
  Eye,
  EyeOff,
  Check,
  X,
  FileText,
  Loader2,
} from 'lucide-react';
import type { BackupManifest, RestoreReport } from '../types';
import { api, ApiError } from '../utils/api';

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

// Passphrase strength: 0–4 from length (≥8, ≥14), mixed case+digits, and a symbol.
function strength(pw: string): number {
  if (!pw) return 0;
  let s = 0;
  if (pw.length >= 8) s++;
  if (pw.length >= 14) s++;
  if (/[A-Z]/.test(pw) && /[a-z]/.test(pw) && /[0-9]/.test(pw)) s++;
  if (/[^A-Za-z0-9]/.test(pw)) s++;
  return Math.min(4, s);
}
const STR_LBL = ['', 'Weak', 'Fair', 'Good', 'Strong'];

const count = (m: BackupManifest, key: string) => m.counts[key] ?? 0;

type KeySource = 'passphrase' | 'server' | 'unknown';

// The .kgbak envelope header is cleartext: magic "KGBK" (4 bytes) | version (1) | keySource (1).
// Reading byte 5 locally tells us whether a passphrase is needed — no server round-trip, no decryption.
async function detectKeySource(file: File): Promise<KeySource> {
  try {
    const bytes = new Uint8Array(await file.slice(0, 6).arrayBuffer());
    const isKgbk = bytes.length >= 6 && bytes[0] === 0x4b && bytes[1] === 0x47 && bytes[2] === 0x42 && bytes[3] === 0x4b;
    if (!isKgbk) return 'unknown';
    return bytes[5] === 2 ? 'server' : bytes[5] === 1 ? 'passphrase' : 'unknown';
  } catch {
    return 'unknown';
  }
}

type BackupMode = 'passphrase' | 'server';

/* ============================ Backup card (safe) ============================ */
function BackupCard({ onDownloaded }: { onDownloaded: (filename: string) => void }) {
  const [mode, setMode] = useState<BackupMode>('passphrase');
  const [pw, setPw] = useState('');
  const [pw2, setPw2] = useState('');
  const [show, setShow] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  const s = strength(pw);
  const match = !!pw && !!pw2 && pw === pw2;
  const tooShort = pw.length > 0 && pw.length < 8;
  const canDownload = mode === 'server' ? !busy : (pw.length >= 8 && match && !busy);

  const handleBackup = async () => {
    setError(null);
    setDone(null);
    setBusy(true);
    try {
      const { blob, filename } = await api.createBackup(
        mode === 'server' ? { useServerKey: true } : { passphrase: pw },
      );
      downloadBlob(blob, filename);
      setDone(filename);
      onDownloaded(filename);
      setPw('');
      setPw2('');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Backup failed.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="card safe">
      <div className="card-top">
        <span className="card-ic"><Download size={20} /></span>
        <div>
          <div className="card-tt">Create a backup<span className="card-tag safe">Safe</span></div>
          <p className="card-desc">
            A complete, <b>encrypted snapshot</b> of this instance — workflows, versions, schedules and
            credentials. Not a shareable bundle.
          </p>
        </div>
      </div>
      <div className="card-body">
        <div className="bkr-modes" role="radiogroup" aria-label="Backup protection">
          <button
            type="button"
            role="radio"
            aria-checked={mode === 'passphrase'}
            className={'bkr-mode' + (mode === 'passphrase' ? ' on' : '')}
            onClick={() => { setMode('passphrase'); setDone(null); }}
          >
            Passphrase
          </button>
          <button
            type="button"
            role="radio"
            aria-checked={mode === 'server'}
            className={'bkr-mode' + (mode === 'server' ? ' on' : '')}
            onClick={() => { setMode('server'); setDone(null); }}
          >
            This server's key
          </button>
        </div>

        {mode === 'passphrase' ? (
          <>
            <div className="callout warn">
              <span className="co-ic"><AlertTriangle size={16} /></span>
              <span className="co-t">
                <b>Your passphrase can't be recovered.</b> It encrypts the file's secrets. Lose it and the
                backup is permanently unreadable — store it somewhere safe. Portable: restorable on any server.
              </span>
            </div>

            <div className="field">
              <label htmlFor="bkr-pass">Passphrase</label>
              <div className="inp mono">
                <input
                  id="bkr-pass"
                  type={show ? 'text' : 'password'}
                  value={pw}
                  onChange={(e) => { setPw(e.target.value); setDone(null); }}
                  placeholder="At least 8 characters"
                  aria-label="Backup passphrase"
                />
                <button type="button" className="eye" onClick={() => setShow((v) => !v)} aria-label={show ? 'Hide passphrase' : 'Show passphrase'}>
                  {show ? <EyeOff size={17} /> : <Eye size={17} />}
                </button>
              </div>
              {pw && (
                <>
                  <div className={'meter s' + s}><span /><span /><span /><span /></div>
                  <div className="meter-row">
                    <span className="meter-lbl">Strength: <b>{STR_LBL[s] || 'Weak'}</b></span>
                    {tooShort && <span className="match no"><AlertTriangle size={13} /> Too short</span>}
                  </div>
                </>
              )}
            </div>

            <div className="field">
              <label htmlFor="bkr-pass2">Confirm passphrase</label>
              <div className="inp mono">
                <input
                  id="bkr-pass2"
                  type={show ? 'text' : 'password'}
                  value={pw2}
                  onChange={(e) => { setPw2(e.target.value); setDone(null); }}
                  placeholder="Re-enter to confirm"
                  aria-label="Confirm backup passphrase"
                />
              </div>
              {pw2 && (
                <div className="meter-row" style={{ marginTop: 9 }}>
                  <span className={'match ' + (match ? 'ok' : 'no')}>
                    {match ? <Check size={13} /> : <X size={13} />}
                    {match ? 'Passphrases match' : "Passphrases don't match"}
                  </span>
                </div>
              )}
            </div>
          </>
        ) : (
          <div className="callout info">
            <span className="co-ic"><Shield size={16} /></span>
            <span className="co-t">
              Encrypted with <b>this server's key</b> — no passphrase to remember. The trade-off: it can only
              be restored <b>on this server</b> (while its key is unchanged). Not for migrating to another host.
            </span>
          </div>
        )}

        <div className="btn-row">
          <button className="btn teal" disabled={!canDownload} onClick={handleBackup} aria-label="Download backup">
            {busy ? <span className="spin-i"><Loader2 size={16} /></span> : <Download size={16} />}
            {busy ? 'Preparing…' : 'Download backup'}
          </button>
          {mode === 'passphrase' && !canDownload && !busy && (
            <span className="meter-lbl">Set a matching passphrase of 8+ characters to enable.</span>
          )}
        </div>

        {done && (
          <div className="success" role="status">
            <Check size={16} />
            <span>
              Downloaded <span className="mono">{done}</span>
              {mode === 'passphrase'
                ? ' — keep it and your passphrase together and safe.'
                : ' — restorable on this server only.'}
            </span>
          </div>
        )}
        {error && <div className="restore-err" role="alert">{error}</div>}
      </div>
    </div>
  );
}

/* ============================ Restore card (destructive) ============================ */
interface RestoreCardProps {
  armed: boolean | null;
  onDisarm: () => void;
}

function RestoreCard({ armed, onDisarm }: RestoreCardProps) {
  const [file, setFile] = useState<File | null>(null);
  const [pw, setPw] = useState('');
  const [show, setShow] = useState(false);
  const [over, setOver] = useState(false);
  const [inspecting, setInspecting] = useState(false);
  const [manifest, setManifest] = useState<BackupManifest | null>(null);
  const [confirm, setConfirm] = useState('');
  const [restoring, setRestoring] = useState(false);
  const [report, setReport] = useState<RestoreReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Detected locally from the file header; null while reading. Drives whether a passphrase is asked at all.
  const [keySource, setKeySource] = useState<KeySource | null>(null);
  const inputRef = useRef<HTMLInputElement | null>(null);

  const isDisarmed = armed === false;
  // Only a confirmed server-key backup hides the passphrase; while detecting (null) we default to asking.
  const needsPassphrase = keySource !== 'server';

  // A new file or passphrase invalidates a prior preview/confirm so a stale manifest can't be restored.
  const resetPreview = () => { setManifest(null); setReport(null); setConfirm(''); setError(null); };
  const pick = (f: File | null | undefined) => {
    if (!f) return;
    setFile(f);
    resetPreview();
    setKeySource(null);
    void detectKeySource(f).then(setKeySource);
  };
  const reset = () => { setFile(null); setPw(''); setKeySource(null); resetPreview(); };

  const handleInspect = async () => {
    resetPreview();
    if (!file) return;
    setInspecting(true);
    try {
      // Passphrase may be empty: a server-key backup is auto-detected and needs none. For a
      // passphrase-protected backup the server returns a precise 400 ("enter the passphrase…").
      setManifest(await api.inspectBackup(file, pw));
    } catch (err) {
      if (err instanceof ApiError && err.status === 400) {
        setError(err.message || "Couldn't decrypt — check the passphrase, or the file isn't a valid .kgbak backup.");
      } else if (err instanceof ApiError) {
        setError(err.message);
      } else {
        setError(err instanceof Error ? err.message : 'Could not read the backup.');
      }
    } finally {
      setInspecting(false);
    }
  };

  const handleRestore = async () => {
    setError(null);
    if (!file) return;
    setRestoring(true);
    try {
      const result = await api.restoreBackup(file, pw, true);
      setReport(result);
      setManifest(null);
      setConfirm('');
    } catch (err) {
      if (err instanceof ApiError && err.status === 412) {
        setError('Restore is blocked while the runtime is armed. Disarm the runtime and try again.');
      } else if (err instanceof ApiError) {
        setError(err.message);
      } else {
        setError(err instanceof Error ? err.message : 'Restore failed.');
      }
    } finally {
      setRestoring(false);
    }
  };

  // For a passphrase backup, require a non-empty passphrase so the user doesn't trigger a pointless
  // "enter the passphrase" round-trip. A detected server-key backup needs none.
  const canInspect = !!file && !inspecting && (!needsPassphrase || pw.length > 0);
  const confirmOK = confirm.trim().toUpperCase() === 'RESTORE';
  const canRestore = !!manifest && isDisarmed && confirmOK && !restoring && !report;

  return (
    <div className="card danger">
      <div className="card-top">
        <span className="card-ic"><Upload size={20} /></span>
        <div>
          <div className="card-tt">Restore from a backup<span className="card-tag danger">Destructive</span></div>
          <p className="card-desc">
            Restoring <b>replaces all current state</b> with the backup's contents. A safety backup of the
            current state is taken automatically before anything changes.
          </p>
        </div>
      </div>
      <div className="card-body">
        {/* step 1 — file */}
        {!file ? (
          <div
            className={'drop' + (over ? ' over' : '')}
            onClick={() => inputRef.current?.click()}
            onDragOver={(e) => { e.preventDefault(); setOver(true); }}
            onDragLeave={() => setOver(false)}
            onDrop={(e) => { e.preventDefault(); setOver(false); pick(e.dataTransfer.files?.[0]); }}
          >
            <span className="dz-ic"><Upload size={22} /></span>
            <div className="dz-t">Drop a backup file or <em>browse</em></div>
            <div className="dz-s">Accepts a single <span className="mono">.kgbak</span> file</div>
            <input
              ref={inputRef}
              type="file"
              accept=".kgbak"
              style={{ display: 'none' }}
              onChange={(e) => pick(e.target.files?.[0])}
              aria-label="Backup file"
            />
          </div>
        ) : (
          <div className="filechip">
            <span className="fc-ic"><FileText size={18} /></span>
            <div style={{ minWidth: 0 }}>
              <div className="fc-n">{file.name}</div>
              <div className="fc-s">
                {(file.size / 1048576).toFixed(1)} MB · {keySource === 'server' ? 'server-key encrypted' : keySource === 'passphrase' ? 'passphrase-protected' : 'encrypted'}
              </div>
            </div>
            <button className="fc-x" onClick={reset} aria-label="Remove file"><X size={15} /></button>
          </div>
        )}

        {/* step 2 — (passphrase if needed) + inspect */}
        {file && !manifest && (
          <>
            {keySource === 'server' ? (
              <div className="callout info" style={{ marginTop: 18 }}>
                <span className="co-ic"><Shield size={16} /></span>
                <span className="co-t">
                  This backup is encrypted with <b>this server's key</b> — no passphrase needed. It opens only
                  on this host.
                </span>
              </div>
            ) : (
            <div className="field" style={{ marginTop: 18 }}>
              <label htmlFor="bkr-rpass">Passphrase <span className="hint">the one used when this backup was created</span></label>
              <div className="inp mono">
                <input
                  id="bkr-rpass"
                  type={show ? 'text' : 'password'}
                  value={pw}
                  onChange={(e) => { setPw(e.target.value); resetPreview(); }}
                  placeholder="Enter the backup's passphrase"
                  aria-label="Restore passphrase"
                />
                <button type="button" className="eye" onClick={() => setShow((v) => !v)} aria-label={show ? 'Hide passphrase' : 'Show passphrase'}>
                  {show ? <EyeOff size={17} /> : <Eye size={17} />}
                </button>
              </div>
            </div>
            )}
            <div className="btn-row" style={{ marginTop: 16 }}>
              <button className="btn ghost" disabled={!canInspect} onClick={handleInspect} aria-label="Inspect backup">
                {inspecting ? <span className="spin-i"><Loader2 size={16} /></span> : <Shield size={16} />}
                {inspecting ? 'Decrypting…' : 'Inspect backup'}
              </button>
              <span className="meter-lbl">We decrypt and show what's inside before you commit.</span>
            </div>
          </>
        )}

        {/* step 3 — inspection result + guards + confirm */}
        {manifest && (
          <>
            <div className="inspect">
              <div className="in-head">
                <span className="vok"><Check size={12} /> Decrypted &amp; verified</span>
                <span className="in-tt">Backup contents</span>
              </div>
              <div className="in-grid">
                <div className="in-cell">
                  <div className="in-k">Created</div>
                  <div className="in-v">{new Date(manifest.createdAtUtc).toLocaleString()}</div>
                </div>
                <div className="in-cell">
                  <div className="in-k">Engine</div>
                  <div className="in-v mono" style={{ fontSize: 13 }}>{manifest.engineVersion} <span className="u">· format v{manifest.formatVersion} · {manifest.databaseProvider}</span></div>
                </div>
                <div className="in-cell">
                  <div className="in-k">Workflows</div>
                  <div className="in-v">{count(manifest, 'workflow-definitions.json')} <span className="u">· {count(manifest, 'workflow-versions.json')} versions</span></div>
                </div>
                <div className="in-cell">
                  <div className="in-k">Schedules</div>
                  <div className="in-v">{count(manifest, 'schedules.json')} <span className="u">· {count(manifest, 'polling-triggers.json')} polling</span></div>
                </div>
                <div className="in-cell">
                  <div className="in-k">Credentials</div>
                  <div className="in-v">{count(manifest, 'credentials.json')} <span className="u">· {count(manifest, 'notification-channels.json')} channels</span></div>
                </div>
                <div className="in-cell">
                  <div className="in-k">Run history</div>
                  <div className="in-v">{manifest.includesRunHistory ? 'Included' : 'Not included'}{file && <span className="u"> · {(file.size / 1048576).toFixed(1)} MB</span>}</div>
                </div>
                <div className="in-cell" style={{ borderBottom: 'none', borderRight: 'none', gridColumn: '1 / -1' }}>
                  <div className="in-k">Encrypted with</div>
                  <div className="in-v">{manifest.keySource === 'ServerKey' ? "This server's key (host-bound)" : 'A passphrase (portable)'}</div>
                </div>
              </div>
            </div>

            <div className="guard">
              <div className="g-h">Before restoring — 2 checks</div>

              <div className={'g-step' + (isDisarmed ? ' done' : '')}>
                <span className="g-num">{isDisarmed ? <Check size={14} /> : '1'}</span>
                <div className="g-body">
                  <div className="g-t">{isDisarmed ? 'Runtime disarmed' : 'Disarm the runtime'}</div>
                  <div className="g-s">{isDisarmed ? 'No live triggers will fire during the restore.' : 'Stop live triggers so nothing runs mid-restore.'}</div>
                </div>
                <div className="g-act">
                  {isDisarmed
                    ? <span className="g-done-tag"><Check size={13} /> Done</span>
                    : <button className="g-mini" onClick={onDisarm} aria-label="Disarm the runtime">Disarm now</button>}
                </div>
              </div>

              <div className="g-step done">
                <span className="g-num"><Check size={14} /></span>
                <div className="g-body">
                  <div className="g-t">Safety backup is automatic</div>
                  <div className="g-s">Current state is snapshotted to <span className="mono" style={{ fontSize: 12 }}>auto-pre-restore.kgbak</span> before any change.</div>
                </div>
              </div>
            </div>

            {/* final confirm */}
            <div className="field confirm-field" style={{ marginTop: 18 }}>
              <div className="confirm-note">To confirm this irreversible replace, type <code>RESTORE</code> below.</div>
              <div className="inp">
                <input
                  value={confirm}
                  onChange={(e) => setConfirm(e.target.value)}
                  placeholder="RESTORE"
                  disabled={!isDisarmed}
                  aria-label="Type RESTORE to confirm"
                />
              </div>
            </div>

            <div className="btn-row" style={{ marginTop: 16 }}>
              <button className="btn danger" disabled={!canRestore} onClick={handleRestore} aria-label="Restore backup">
                {restoring ? <span className="spin-i"><Loader2 size={16} /></span> : <ShieldAlert size={16} />}
                {restoring ? 'Restoring…' : 'Restore this instance'}
              </button>
              {!isDisarmed && <span className="match no"><AlertTriangle size={13} /> Disarm the runtime first</span>}
            </div>
          </>
        )}

        {report && (
          <div className="restored" role="status">
            <span className="r-ic"><Check size={16} /></span>
            <div>
              <div className="r-t">Instance restored from backup</div>
              <div className="r-s">
                {count(report.manifest, 'workflow-definitions.json')} workflows and {count(report.manifest, 'credentials.json')} credentials
                replaced the previous state. A pre-restore safety backup was saved to{' '}
                <span className="mono">{report.preRestoreBackupPath}</span>. Re-arm the runtime when you're ready.
              </div>
            </div>
          </div>
        )}

        {error && <div className="restore-err" role="alert">{error}</div>}
      </div>
    </div>
  );
}

/* ============================ Panel ============================ */
interface BackupRestorePanelProps {
  /** Shared runtime-arming state (also drives the top-bar pill). `null` while unknown. */
  armed?: boolean | null;
  /** Disarm the runtime — flips the same shared state the top bar shows. */
  onDisarm?: () => void;
}

/**
 * "Backup & Restore" admin panel (lives in the Settings view). A safe, two-field "Create a backup" card
 * downloads a passphrase-encrypted .kgbak snapshot of the whole instance (secrets included). The
 * destructive "Restore" card is stepped: choose a file → inspect-preview → disarm + type-to-confirm,
 * blocked while the runtime is armed.
 */
export function BackupRestorePanel({ armed = null, onDisarm }: BackupRestorePanelProps) {
  const [last, setLast] = useState<{ filename: string; at: string } | null>(null);

  return (
    <div className="bkr">
      <div className="bkr-head">
        <h2><span className="bkr-hi"><Database size={20} /></span> Backup &amp; Restore</h2>
        <p>
          Save or replace this instance's full state. A backup is a complete, <b>encrypted snapshot</b> — it
          contains your secrets, so it lives here under instance settings rather than with failure alerts.
        </p>
      </div>

      <div className="bkr-grid split">
        <BackupCard onDownloaded={(filename) => setLast({ filename, at: new Date().toLocaleString() })} />
        <RestoreCard armed={armed} onDisarm={() => onDisarm?.()} />
      </div>

      {last && (
        <div className="lastbk">
          <span className="lb-ic"><Check size={15} /></span>
          <div>
            <div className="lb-t">Last backup downloaded</div>
            <div className="lb-s mono">{last.filename}</div>
          </div>
          <div className="lb-meta">{last.at}<br />by you</div>
        </div>
      )}
    </div>
  );
}
