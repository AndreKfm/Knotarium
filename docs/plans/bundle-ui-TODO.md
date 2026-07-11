# Bundle UI — Frontend TODO

Front-end work to make the `.kgbundle` integration-library feature usable from the app. The **backend is
complete and tested** (export/import/verify/rebind/conflict-guard, 121 tests); there is currently **no UI** —
the two endpoints are only reachable via curl/Postman. This doc tracks the UI work so it can be picked up or
deferred as a tracked group.

Backend reference: [bundle-installer-adrs.md](../bundle-installer-adrs.md) · pipeline lives in
`Backend/Knotarium.Api/Services/Bundles/`.

**Working dir:** `Frontend/` · **Test runner:** `npx vitest run` · **Type gate:** `npx tsc -b` ·
**Lint:** `npx eslint <files>`

---

## Endpoints to consume (already live)

| Endpoint | Shape |
|---|---|
| `POST /api/bundles/export` | JSON `BundleManifest` in → `.kgbundle` file out (`application/zip`) |
| `POST /api/bundles/install` | multipart: `bundle` (file), `allowProvisional` (bool), `credentialBindings` (JSON `{slot:credId}`) |

Install response (JSON): `installed`, `installedPackages[]`, `skippedPackages[]`, `importedWorkflows[]`,
`requiredCredentialSlots[]`, `reboundCredentialSlots[]`, `unboundCredentialSlots[]`, `conflictingPackages[]`,
`verification[]` (per-package: id, hashMatches, signatureStatus, trustLevel, status, installable),
`blocking[]`. Status codes: **200** installed · **422** verification rejected · **409** version conflict ·
**400** malformed.

---

## ☑ Phase 1 — API client methods — DONE

Added to `src/utils/api.ts`:

- [x] `exportBundle(manifest): Promise<Blob>` — raw `fetch` + `response.blob()` (not `handleResponse`, which
      assumes JSON); a 400 still routes through `handleResponse` to throw an `ApiError` with the server message.
      The download trigger (`URL.createObjectURL` + anchor) is deferred to the Phase 3 export UI.
- [x] `installBundle(file, { allowProvisional?, credentialBindings? })` — `FormData` POST. Returns
      `{ status, result }` so the UI gets the full report on **200 / 422 / 409** (all carry it); 400/5xx throw
      `ApiError`. (`handleResponse`'s `ApiError` already preserves `.status`/`.data`, but returning the parsed
      body for the three report-bearing statuses is what the screen needs.)
- [x] `listCredentials(): Promise<CredentialSummary[]>` — typed `id`+`name` view over the masked
      `GET /api/credentials`, for the slot-binding selectors.
- [x] Types in `src/types.ts`: `BundleInstallResponse`, `BundlePackageVerification`, `BundleWorkflowInstall`,
      `BundleCredentialSlot`, `CredentialSummary`. **Gotcha baked in:** the API has no global
      `JsonStringEnumConverter`, so `signatureStatus` / `trustLevel` / `status` arrive as **integers** — typed as
      `number` with the legend in JSDoc + the `*_LABELS` maps in `BundleInstaller`.

---

## ☑ Phase 2 — Install flow — DONE

`src/components/BundleInstaller.tsx`, wired as a `'bundles'` view in `src/App.tsx`.

- [x] Registered the `'bundles'` view in `src/App.tsx`: extended `type View`, both persistence allowlists
      (`currentView` guard + `lastNonExecutionView` ladder), a "Bundles" nav button (`Package` icon), and the
      view-switch mount.
- [x] `BundleInstaller.tsx` — drag/drop file picker (mirrors `OpenApiImporter.tsx`).
- [x] **Verification report panel** — table over `verification[]` showing the three axes **kept distinct**:
      trust level, signature status, and hash-match (a `MISMATCH` renders boldly, separate from
      untrusted/provisional). Non-installable rows are tinted + shield-iconed.
- [x] **Credential-slot binding** — selector per `requiredCredentialSlots[]` → real credential id; feeds
      `credentialBindings` on the next install. Unbound is allowed; the success banner warns about any
      `unboundCredentialSlots`.
- [x] **Result handling** — 200 success summary (installed/skipped/imported/rebound/unbound); **422** verification
      rejected + report; **409** `conflictingPackages` with the "already installed, different bytes" message and
      **no** silent-overwrite option (ADR-2).
- [x] `allowProvisional` checkbox, default **off**.
- [x] `BundleInstaller.test.tsx` — 6 tests (200 / 422 / 409 / slot-binding re-install / api-error / disabled
      button); all green.

**Realized flow** (given the one-call backend): the install response carries `requiredCredentialSlots` even on a
422/409 (which write nothing), so the natural sequence is *upload → install → (for an unsigned bundle) 422 stop →
bind slots + tick allowProvisional → re-install → 200*. A true verify-only preview is still the deferred backend
addition (ADR-4).

---

## ☑ Phase 3 — Export authoring — DONE

`src/components/BundleExporter.tsx`, surfaced as the **Export** tab of the Bundles view
(`src/components/BundlesView.tsx` wraps Install | Export under the single nav entry).

- [x] Manifest assembly: workflow multi-select (each → `BundleWorkflowRef { key: WorkflowDefinitionId,
      role: 'primary', ref: '<key>.json' }`) with a **Select all / Clear** toggle.
- [x] **Packages are auto-derived, not picked.** A node's `type` *is* a package id (`WorkflowCompiler` resolves
      `GetManifestAsync(new NodePackageId(node.Type))`), so the bundle's packages are exactly the bundleable
      packages used by the selected workflows, pinned to their latest installed version. **Built-in nodes are
      excluded** — `/api/node-packages` concatenates built-ins (source `"Built-in …"`) with custom DB packages,
      but only custom packages live in the export registry, so a built-in like `errorTrigger` would 400 with
      *"No available package satisfies …"*. The filter (`source` not starting with `"Built-in"`) drops them.
- [x] `CredentialSlots` declared via an add/remove editor (slot, type, display name, checklist).
- [x] Metadata form: bundleId, version, name, publisher, tags, category (`schemaVersion: 1`,
      `minEngineVersion: '0.9.0'`, `provenance.source: 'local'` defaulted).
- [x] `exportBundle(manifest)` → client-side `downloadBlob` (`createObjectURL` + anchor), filename
      `<bundleId>-<version>.kgbundle`. Export errors (e.g. `BundlePackageNotFoundException` → 400) surface inline.
- [x] `BundleExporter.test.tsx` — 5 tests (id required / needs a workflow-or-package / manifest assembly +
      version pin / credential slots / error surfacing); all green.

**Design calls made:**
- *Where it lives* — folded into the existing **Bundles** view as an Install | Export tab toggle, not a separate
  nav entry. Consistent with Phase 2 and keeps the two halves of the same feature together.
- *Package refs* — **auto-derived from the selected workflows** (node `type` = package id), not hand-picked, and
  filtered to bundleable (non-built-in) registry packages. Shown read-only so the author can see what's included.

**Honest limitation surfaced in the UI:** export does **not** rewrite a workflow's real credential ids into
`slot:` placeholders — the included workflows must already use `slot:<name>` references matching the declared
slots for the bundle to be portable. (A rewrite-on-export step would be a backend addition.)

---

## Sequencing & notes

- **Status:** Phases 1, 2 & 3 are **implemented and tested** — install *and* export are usable end-to-end from
  the Bundles view (Install | Export tabs).
- **Recommended order:** Phase 1 → Phase 2 → Phase 3. Phase 2 delivers a usable feature on its own (install
  bundles others produced); Phase 3 makes the app able to *produce* them.
- Phases 1–2 are mechanical and low-risk. Phase 3 needs a design decision first.
- This is **not** a backend dependency — every backend piece Phase 1–2 needs already exists and is tested.
- Keep the trust/verification surface honest: the backend distinguishes `HashMismatch` (tampered) from
  `Untrusted` (intact, wrong/no key) and `signatureStatus` (`NotPresent`/`PresentUntrusted`/`VerifiedTrusted`) —
  the UI should reflect that distinction, not flatten it to "ok / not ok".
