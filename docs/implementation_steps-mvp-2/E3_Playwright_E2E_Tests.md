# Step E3: Playwright End-to-End Regression Tests

## Goal
Implement a robust Visual E2E regression check using Playwright, verifying the complete workflow cycle from node package compilation to canvas placement, and testing all happy and unhappy security paths.

## Proposed Changes

### Happy Path E2E Speclist
Verify the core visual workflow:
1. Navigate to the Node Editor page.
2. Edit C# code draft in Monaco, compile, and execute successful sandbox tests.
3. Publish package and verify it is hot-loaded successfully.
4. Mount the custom node onto the React Flow canvas, save, and execute the workflow.
5. Track and verify live SSE state progression.

### Unhappy & Security Path E2E Speclist
Incorporate strict visual checks for regression coverage:
1. **Banned API Rejection**: Write C# draft using forbidden namespaces (e.g. `System.IO.File`). Assert that the banned API analyzer refuses compilation and prevents publishing (§5, §13).
2. **Publish Without Testing Refused**: Attempt to publish a draft before running sandbox verification. Assert that the Node Editor gate rejects publishing (§6).
3. **Signature Failure Refused**: Attempt to install a package ZIP file featuring an invalid or modified Ed25519 signature. Assert that `/api/node-packages/install` rejects loading (§5, §13).
4. **SSE Reconnect with Replay**: Simulate browser network disconnection during active execution runs. Assert that the frontend reconnects and successfully replays missing state events using `Last-Event-ID` replay (§9).

---

## Constraints from Architecture
- **E2E Isolation**: Playwright test suites must manage their own backend and database lifecycles, ensuring zero-dependency, isolated execution checks (§15).
- **Security Assertions**: E2E suites must assert that all security gates (signature checks, banned API blocks, session verification) fail gracefully with correct visual alarms (§13).
- **Fidelity Verification**: SSE re-stream replays must be assertable up to the exact execution sequence length (§9).
