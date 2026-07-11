# Step E2: Secrets Hardening & Egress Filters

## Goal
Implement secure credential handling, configure encryption at rest, write a SecretValue masking wrapper, and enforce outbound network domain filters.

## Proposed Changes

### Key Management & Encryption At Rest
Configure credential storage to encrypt values at rest using AES-256 (§11):
- **Key Management**: The encryption key is managed strictly by the host, sourced dynamically from environment variables or a platform keystore. **Under no circumstances is the key stored in the database** (§11).

### SecretValue Wrapper Structures
Create a secure, custom object wrapper structure:
- Prevent accidental exposures in logs by returning `"***"` in `ToString()` implementations (§11).
- Filter out raw strings from structured logging pipelines (§11).
- Coerce to raw strings strictly inside capability accessors at the last possible moment (§11).

### Dynamic Outbound Egress Filters
Configure outbound network requests in the `http` capability:
- Restrict outbound network calls to a configurable allowlist/blocklist of target domains (§13).
- Deny calls targeting loopback addresses or unauthorized internal IPs (§13).

---

## Constraints from Architecture
- **Key Separation**: The database must never contain host key materials; all keys must reside within isolated environment scopes or platform keystores (§11).
- **Leak Prevention**: Secret value properties must mask data by default, returning raw values strictly inside designated HTTP headers (§11).
- **Network Egress Evasion**: Network egress allowlists must be evaluated prior to DNS resolution, preventing exfiltration bypasses (§13).
