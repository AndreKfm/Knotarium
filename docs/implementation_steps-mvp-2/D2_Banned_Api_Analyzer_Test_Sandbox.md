# Step D2: Roslyn Banned API Analyzer & Test Sandbox Gate

## Goal
Implement the Roslyn banned API analyzer, build a capability-recording test sandbox, and enforce the mandatory publish verification gate.

## Proposed Changes

### Static Banned API Analyzer
Implement `BannedApiRoslynAnalyzer.cs`:
- Evaluates submitted compiled node code drafts at compile time.
- Hard-fails on use of forbidden namespaces (`System.IO`, `System.Diagnostics`, `System.Reflection.Emit`, `System.Net.Sockets`, and static mutable states outside the executor) (§5, §13).
- **Strict Constraint**: There is **no override flag**; violations represent permanent blockages (§5, §13).

### Capability-Recording Sandbox
Build the test executor at `POST /api/node-editor/test`:
1. Compiles the draft source and loads it into a temporary `CollectibleAssemblyLoadContext` (§6).
2. Runs cases inside a **Mock INodeContext sandbox** that dynamically intercepts and **records every capability invocation** (§6).
3. Evaluates standard assertions.

### Mandatory Publish Gate
Cross-check manifest capability parameters:
- The backend compiler gate must verify that all capabilities recorded during the test runs match the list declared inside `manifest.yaml` (§6).
- **If an undeclared capability is invoked during tests, the test run fails** (§6).
- `POST /api/node-packages/publish` must reject publication requests unless tests have passed successfully within the current editor session (§6).

---

## Constraints from Architecture
- **Analyzer Invariant**: Static banned API restrictions are absolute; no configuration override flags are permitted (§5, §13).
- **Isolation Checks**: Sandbox test runs must record and audit all capability calls, failing execution immediately if undeclared dependencies are accessed (§6).
- **Session Gate**: Publish APIs must enforce the test-before-publish gate, preventing unverified assembly loads (§6).
