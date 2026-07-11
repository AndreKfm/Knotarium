# Bundle Installer — Architecture Decisions

Decisions for the `.kgbundle` integration-library pipeline (`Backend/KnotGarden.Api/Services/Bundles/`).
This doc separates **what is implemented today** (a single synchronous, transactional install) from
**decisions deferred to a future "staged installer"** (asynchronous, approvable, auditable). It exists so the
deferred items aren't re-litigated and so the rationale survives.

Guiding rule for every decision below: *prefer the option that is deterministic, non-destructive, recoverable
after a crash, and does not silently increase executable-code privileges.*

---

## Current shape (baseline)

Install is one synchronous operation: `read → verify (pure gate) → pre-flight conflict check → apply (one DB
transaction) → commit`. There is **no persisted installation lifecycle** (no `Pending/Staged/Active` entity).
Imported workflows are created **inactive**. Packages are embedded in the archive (self-contained bundles only).

The ADRs below are written against that baseline.

---

## ADR-1 — Trust = facts + derived policy

**Decision.** Store the *factual* dimensions (resolved source, signature outcome) separately from the *policy*
outcome (trust level), and re-derive policy at install time against the **installer's** trusted keys rather
than trusting the lock blindly.

**Implemented.**
- The lock persists `ResolvedSource` + `TrustLevel` per package; the package file carries the signature.
- `BundleVerifier` re-hashes and re-derives trust at install (`BundleTrust.Derive`).
- `BundleVerifier` now reports a factual `PackageSignatureStatus` axis (`NotPresent` / `PresentUntrusted` /
  `VerifiedTrusted`) alongside the policy `TrustLevel` and the `BundleVerificationStatus`.

**Known limitation / deferred.** The current crypto model (`PackageSigner.Verify`) only answers *"does this
verify against a trusted key"*. So a **cryptographically invalid** signature and a **valid-but-untrusted-signer**
signature both collapse into `PresentUntrusted`. Splitting them requires the package to carry its signer's
public key, then checking crypto-validity against *that* key separately from trust. Worthwhile for audit, not
yet done.

**Deferred (needs a persisted install record).** Persist, per installation: `origin`, `signatureStatus`,
`effectiveTrustAtInstall`, and `trustPolicyVersion`. Without `trustPolicyVersion` you cannot later explain why
a past install was allowed. There is no installation/audit entity today, so there is nowhere to write these —
build the entity first.

---

## ADR-2 — Never silently upgrade or downgrade; same-version-different-hash is always blocked

**Decision.** Reuse only an *exact* match. A package whose version is already installed **with different
bytes** is a hard conflict, never a silent skip. Do not implement upgrade-with-undo (downgrade is not a
reliable undo once migrations/caches/runtime state exist).

**Implemented.**
- The manifest already separates authored requirement (`VersionConstraintOrPin`), resolved version, and the
  pinned `Sha256` in the lock.
- Install runs a read-only **pre-flight conflict check**: for each locked package, if the same version is
  already installed but its bytes hash differently from the lock's `Sha256`, the **whole install is rejected
  before any write** (`ConflictingPackages` in the result; HTTP 409). Identical bytes ⇒ idempotent reuse.

**Deferred.** Richer compatibility resolution ("installed newer but compatible ⇒ reuse + report", node-contract
compatibility checks). Today the comparison is exact-version + exact-hash; compatibility-range reuse is a
future enhancement and should still require validation, never silent substitution.

---

## ADR-3 — Credentials: symbolic slot identity, never reuse by name

**Decision.** A bundled workflow references credentials only by symbolic `slot:<Slot>` placeholders. Never
auto-reuse a credential just because a display name matches — name equality alone crosses orgs, scopes, and
prod/test boundaries.

**Implemented.**
- `BundleCredentialRebinder` rewrites `slot:<Slot>` → real credential id from a **caller-supplied** binding map
  only. Unbound slots are left as placeholders and reported (`UnboundCredentialSlots`); install still succeeds
  because imported workflows are inactive.
- Unattended/default behavior is conservative: bind only what the caller explicitly maps, otherwise leave a
  placeholder. No name-based matching anywhere.

**Deferred (preflight UX).** Candidate *suggestion* by structural compatibility (credential type, provider,
schema version, required capabilities, optional tags) — "exactly one compatible ⇒ suggest reuse; multiple ⇒
require selection". This needs the slot model enriched with `provider` / `requiredCapabilities` and a preflight
UI. Suggestion only; never automatic reuse outside a known same-lineage binding.

---

## ADR-4 — Saga states: verification is an invariant, not a state

**Decision.** Do not create a `Verified` lifecycle state merely to expose an implementation step. Verification
is an *invariant* of being staged (payloads present, hashes match, signatures/provenance evaluated,
compatibility checked). Introduce a durable state only when recovery/compensation differs, an external actor can
observe/approve it, or it crosses an irreversible boundary.

**Status.** Not applicable to the current synchronous design — there is no state machine; verification is just
the gate function `BundleVerifier.Verify`.

**Deferred (when install becomes staged/approvable).** A lifecycle like
`Pending → Validated → Staged → AwaitingApproval → Committed → Activating → Active → (Compensating) → Failed`.
`AwaitingApproval` earns a state **only if** preflight confirmation is durable (the user must approve after
staging). Expensive verification steps become saga checkpoints
(`PayloadFetched / IntegrityVerified / AnalyzerVerified / CompatibilityVerified / WorkflowVerified`), not
top-level states. This is the natural home for an install **preview** endpoint (verify-only, no apply).

---

## ADR-5 — Bundle delivery: self-contained vs reference

**Decision.** Support both under one manifest/lock format. Self-contained (payloads embedded) for file export,
offline/air-gapped transfer, and archives. Reference (registry-fetched, hash-pinned) for curated/controlled
catalogues. **Prohibit arbitrary remote URLs** in bundles — references must use registered source identifiers
(`sourceId` + `packageId` + `version`), so install never becomes an SSRF / arbitrary-download surface.

**Status.** Today bundles are **self-contained only** (payloads embedded in `packages/`). `resolvedSource` is a
bare string; there is no fetch path and therefore no current URL/SSRF exposure.

**Deferred.** A reference mode: structured `resolvedSource { sourceId, packageId, version }`, fetched bytes
verified against the lock, `same version / different hash` rejected (consistent with ADR-2), ideally
content-addressed retrieval. Channel defaults — built-in/trusted catalogues: reference; export-to-file /
install-from-file / archive: self-contained; local-dev: either, explicitly marked.

---

## Failure & residue

**Decision.** Do not assume terminal failure implies zero residue; track cleanup separately from failure.

**Current behavior.** The install DB transaction (packages, versions, workflow versions) rolls back cleanly on
failure — effectively "failed clean" for the DB.

**Known partial-residue seam.** `WorkflowPublisher.ImportAsync` writes the workflow **draft** through the
file-based `IWorkflowStore` (`FileWorkflowStore`), which is **outside** the install transaction. A mid-import
failure can therefore leave a draft file behind while the DB rolls back. Documented at the transaction site in
`BundleInstallService`. If install becomes a saga, model this as `cleanupStatus` / `residueDescription` fields
separate from the terminal `Failed` state rather than asserting "no residue".

---

## Activation is not an idempotent flip

**Decision.** A DB status flip is trivially repeatable; loading plugin assemblies into an `AssemblyLoadContext`
is not. Activation must key on a stable installation identity (`installationId + packageId + version + hash`):
if that exact instance is already active, return success; if a different hash/version is active, follow an
explicit side-by-side / replacement / restart policy.

**Status.** Out of scope for install today — imported workflows are created **inactive** and install never
loads assemblies. Relevant to the node-package runtime loader, not the bundle installer, but recorded here so
the constraint isn't lost when activation is wired to bundle install.
