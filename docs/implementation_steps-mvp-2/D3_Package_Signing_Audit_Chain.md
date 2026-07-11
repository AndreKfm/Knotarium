# Step D3: Cryptographic Package Signing & Audit Hashing

## Goal
Implement secure package signature checks, build the append-only, SHA-256 hash-chained database audit log, and co-locate their core cryptographic serialization primitives.

## Proposed Changes

### Ed25519 Package Signing Primitives
Implement cryptographic verification using `PackageSigner.cs` (DR-005):
- **Digest**: Compute a SHA-256 hash over the canonical, deterministic byte sequence of the package components (§17, DR-005).
- **Signature**: Sign digests using Ed25519 (§17, DR-005).
- **Verification models**: Map two verification layers (§13):
  1. **Host-Signed**: Self-built packages are signed by the host's private key during a `publish` transaction.
  2. **Externally Distributed**: Third-party packages are verified against a configured set of trusted public keys managed by the host.

### Tamper-Evident Hashed Audit Chain
Implement `AuditHashChain.cs` calculating cryptographically chained blocks for `AuditEntries` (§13, DR-004):
- `entry_hash = SHA-256(previous_hash || sorted_canonical_json_of_this_entry)` (§13, DR-004).
- Walk and re-verify the full chain from `0x00...00` on database startup.

### Cryptographic Co-Location Rationale
State clearly that **Package Signing and Audit Hashing are co-located in this step**. Both rely on the exact same SHA-256 hashing and sorted, canonical JSON serialization primitives, dramatically reducing code duplication and architectural overhead (DR-004).

---

## Constraints from Architecture
- **Verification Invariant**: Any package lacking a valid Ed25519 signature verified against host keys must be forcefully rejected at load time (§5, §13, DR-005).
- **Audit Tamper Evidence**: The audit log must utilize cryptographic chaining to detect data modification anomalies (§13, DR-004).
- **Canonical Serialization**: Serialization of audit rows must use sorted JSON keys and exclude white spaces to ensure deterministic hash generation (DR-004).
