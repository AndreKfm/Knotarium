# Backup & Restore — TODO

A full-fidelity **backup/restore** for a KnotGarden instance — distinct from `.kgbundle` export (see
[bundle-ui-TODO.md](bundle-ui-TODO.md)). Bundles are curated, signed, **secret-free** distribution artifacts;
a backup is a complete, **secrets-included**, restore-in-place snapshot for disaster recovery / migration.

**Why a bundle can't double as backup:** it omits credentials (by design), execution history, activation state
(imports as *inactive* versions), global settings, workflow groups, and full version history; and its install is
additive/idempotent, not a state-replacing restore. Different goals.

---

## What an instance's state actually is (grounded in the code)

| Where | What | Notes |
|---|---|---|
| **Database** (`AppDbContext`) | workflow definitions + **all versions**, active-version pointers, **credentials (encrypted)**, node packages, schedules, polling triggers/cursors, notification channels, app settings (error-workflow, …), execution instances + journals, dead-letter, server configs, workflow groups | SQLite by default (`KnotGarden.db` + `-wal`/`-shm`), **pluggable to Postgres** (`Database:Provider`) |
| **File workflow store** | draft workflow definitions | `%APPDATA%/KnotGarden/workflows/` — **on disk, not in the DB**; may differ from the DB's published versions |
| **Config / env (NOT backed up as data)** | credential **encryption key** (`Security:Credentials:EncryptionKeyBase64` / `KnotGarden_CREDENTIAL_ENCRYPTION_KEY_BASE64`), DB connection string, trusted signing keys | Credentials in the DB are AES-GCM ciphertext under this key |

**The credential-key problem (drives the design):** a raw DB copy carries credential *ciphertext*. Restored onto
a host with a different at-rest key, every credential is undecryptable. So the backup must **decrypt credentials
with the current key and re-encrypt them under the target host's key at restore** — and the archive itself must be
**passphrase-encrypted**, because between backup and restore the secrets live in it in portable (re-encryptable)
form.

---

## Recommended approach (and the trade-off)

**Logical, provider-agnostic backup.** Serialize each aggregate via EF to a versioned JSON document set + copy the
file-store drafts, pack into one archive, encrypt the archive with a user passphrase (AES-GCM, key derived via
PBKDF2/Argon2). Restore validates a schema/format version, then transactionally replaces state.

- ✅ Works for SQLite **and** Postgres; no WAL/file-lock snapshot hazards; enables a readable manifest + partial
  restore later.
- ⚠️ More code than a file copy: every aggregate must be enumerated (and kept in sync as the schema grows).

*Alternative considered — physical SQLite snapshot* (`VACUUM INTO` a temp file + zip the workflow-store folder):
simpler, but **SQLite-only**, full-replace-only, and still needs the same credential-key handling. Rejected as the
primary path because the app already supports Postgres; keep it in mind as a fast MVP if logical proves too big.

**Format:** mirror the bundle pipeline's shape for consistency —
`backup.json` (manifest: format version, engine version, created-at, db provider, per-aggregate counts) +
`data/<aggregate>.json` entries + `workflows/<id>.json` drafts, all inside a passphrase-encrypted envelope.
Name: `knotgarden-backup-<yyyymmdd-hhmmss>.kgbak`.

---

## Endpoints (to add)

| Endpoint | Shape |
|---|---|
| `POST /api/admin/backup` | JSON `{ passphrase }` in → encrypted `.kgbak` out (`application/octet-stream`) |
| `POST /api/admin/backup/inspect` | multipart: `backup` (file) + `passphrase` → JSON manifest only (decrypt + parse, **no writes**) — powers the restore preview |
| `POST /api/admin/restore` | multipart: `backup` (file), `passphrase`, `confirm` (bool) → JSON restore report. **Destructive**; guarded (see Phase 3) |

Status codes: **200** ok · **400** malformed/bad passphrase · **409** format/version incompatible ·
**412** preconditions not met (e.g. runtime still armed) · **422** validation failed.

---

## ☑ Phase 1 — Backend: backup (snapshot → encrypted archive) — DONE

New `Backend/KnotGarden.Api/Services/Backup/` (mirrors `Services/Bundles/`).

- [x] `BackupArchiveCodec` — inner zip (`backup.json` + `groups.json` + `data/*` + `workflows/*`) wrapped in a
      passphrase-encrypted envelope: AES-256-GCM, key via **PBKDF2-HMAC-SHA256 (600k iters)**. Cleartext header
      (magic `KGBK` | envelope ver | kdf id | iterations | salt) is fed as AES-GCM associated data, so tampering
      with the KDF params fails auth. Wrong passphrase and corruption are indistinguishable by design (both →
      `BackupArchiveException`).
- [x] `BackupManifest` record — format version, engine version, UTC timestamp, db provider, `includesRunHistory`,
      per-aggregate counts. `BackupFormat` holds the format/engine version constants.
- [x] `BackupService.CreateAsync(passphrase, includeRunHistory=false)` — reads workflow defs (DB headers),
      versions, active versions, activation log, node packages (+ versions/compiled assembly), schedules, polling
      triggers, notification channels, app settings, server configs, **and OpenAPI specs** (flattened DTO to avoid
      the EF back-reference cycle); plus file-store drafts + groups. **Credentials and notification-channel configs
      are decrypted via `ICredentialCipher`** and stored plaintext in the (encrypted) archive for re-encryption at
      restore.
- [x] Endpoint `POST /api/admin/backup` (`CreateBackupRequest { passphrase, includeRunHistory? }`) →
      `Results.File(bytes, "application/octet-stream", name)`; 400 on missing passphrase.
- [x] Tests (17, all green): codec round-trip, wrong-passphrase/tamper/truncation rejection, leaf-name/dup guards;
      service round-trip with secrets decrypted, manifest counts match, draft carried, wrong-passphrase fails.

**Deliverable:** a downloadable, passphrase-encrypted, self-describing snapshot. ✅

**Locked decisions (per user):** run history **excluded** by default (`includeRunHistory` flag reserved, not yet
carried); **PBKDF2** for the envelope KDF. **Added beyond the original list:** OpenAPI specs are included (server
configs reference them) — note this is the one aggregate not in the original Phase-1 enumeration.

---

## ☑ Phase 2 — Backend: restore (archive → replace state) — DONE

- [x] `BackupService.InspectAsync(bytes, passphrase)` — decrypt + parse manifest only; no writes. Bad passphrase →
      `BackupArchiveException` (400); incompatible format → `BackupIncompatibleException` (409, carries manifest).
- [x] `BackupService.RestoreAsync(bytes, passphrase, confirm)` — in **one transaction**: validate format version →
      `ExecuteDelete` every managed aggregate (child→parent order) → re-insert in parent→child stages →
      **re-encrypt credentials + notification-channel configs with the current host key** → rewrite file-store
      drafts + groups (after commit — the non-transactional file/DB seam, covered by the pre-restore backup). Run
      history is left untouched (never carried). Rolls back wholesale on any mid-restore failure.
- [x] Safety rails: refuse if runtime **armed** → `BackupRestoreBlockedException(RuntimeArmed)` (412); require
      `confirm: true` → `…(NotConfirmed)` (422); **auto pre-restore backup** written to a temp path (same
      passphrase) before any writes — its path is returned in the `RestoreReport`.
- [x] Endpoints `POST /api/admin/backup/inspect` and `POST /api/admin/restore` (multipart), with the status-code
      mapping above.
- [x] Tests (8 added, 25 total green): inspect-without-write, inspect wrong-passphrase/incompatible; restore
      replaces state + re-encrypts under a **different** host key; armed→412, not-confirmed→422, incompatible→409;
      transactional rollback verified via a malformed (duplicate-version) archive.

---

## ☑ Phase 3 — UI: Backup & Restore (in Settings) — DONE

Lives as a **"Backup & Restore"** section in the existing **Settings** view (`src/App.tsx` `currentView ===
'settings'`, alongside `ErrorWorkflowSetting` + `NotificationChannelManager`) — no new nav entry. Restore is
destructive, so it's gated behind inspect → type-to-confirm.

**API client** (`src/utils/api.ts`):
- [x] `createBackup(passphrase, includeRunHistory?)` → `{ blob, filename }` via raw `fetch` + `response.blob()`
      (filename parsed from `Content-Disposition`); caller downloads. Not `handleResponse` (which assumes JSON).
- [x] `inspectBackup(file, passphrase): Promise<BackupManifest>` and
      `restoreBackup(file, passphrase, confirm): Promise<RestoreReport>` — FormData; non-2xx throws `ApiError`
      (status + body), so the component branches on 400/409/412/422.
- [x] Types in `src/types.ts`: `BackupManifest`, `RestoreReport`.

**Component** `src/components/BackupRestorePanel.tsx`:
- [x] **Backup**: passphrase + confirm-passphrase → "Download backup" → `createBackup` → download. Warns the file
      holds secrets and the passphrase is unrecoverable.
- [x] **Restore**: file picker + passphrase → **inspect first** (preview: created-at, engine/format version, db
      provider, per-aggregate counts) → **type-to-confirm** ("REPLACE") → `restoreBackup`; 412 renders a
      disarm-the-runtime message; renders the post-restore report (counts + pre-restore backup path). Changing the
      file/passphrase invalidates a stale preview.
- [x] `BackupRestorePanel.test.tsx` — 6 tests (all green): backup download, passphrase-mismatch guard, inspect
      preview, restore-gated-by-confirm + success, armed-runtime (412) disarm message, bad-passphrase (400).

Verified in the browser preview: the panel renders in the Settings view with no console errors of its own.

---

## Sequencing & notes

- **Order:** Phase 1 → 2 → 3. Phase 1 alone is independently useful (you can take snapshots via the endpoint
  before any UI exists); Phase 2 makes them restorable; Phase 3 makes both one-click.
- **Keep it honest in the UI** (same principle as the bundle trust surface): be explicit that the archive holds
  secrets, that the passphrase is unrecoverable, and that restore is a full, irreversible (except for the
  auto pre-restore backup) replacement.
- **Don't conflate with bundles.** Backups are not signed, not curated, not meant to be shared; they're whole-
  instance snapshots. Keep the code paths and the UI entry points separate.

### Open decisions (resolve before building)
1. **Execution history in scope?** Journals/dead-letter can be huge. Options: always include / exclude / a
   "include run history" checkbox in the backup UI. *Lean:* exclude by default, opt-in checkbox.
2. **KDF + envelope format** — PBKDF2 (in-box) vs Argon2id (needs a package). *Lean:* PBKDF2-HMAC-SHA256 with a
   high iteration count to avoid a new dependency; revisit if Argon2 is wanted.
3. **Restore granularity** — whole-instance replace only (simplest, recommended first) vs selective
   (workflows-only, settings-only). *Lean:* whole-instance first; the logical format leaves the door open for
   selective later.
4. **Postgres restore** — logical restore works, but confirm transaction scope/permissions on a remote Postgres
   (vs local SQLite) before relying on it.
