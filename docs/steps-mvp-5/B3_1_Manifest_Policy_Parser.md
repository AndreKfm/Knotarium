# Step 2 — B3.1: Manifest Policy Parser

## Goal
Extend the node package manifest parsing logic to support per-node retry policy configurations (`retryPolicy`). The manifest compiler must enforce that custom nodes omitting a side-effect kind are treated as `NonIdempotentSideEffect` by default. Furthermore, we are defining clear, 1-indexed attempt semantics and introducing `triggerOnly` metadata properties to prevent hardcoded type checks.

---

## Invariant Alignment
* **Invariant 5.2 (Default-Deny Side-Effects):** Omitted side-effect kind configurations are treated as `NonIdempotentSideEffect` by default.
* **Metadata-Driven Validation**: We are adding `triggerOnly: true` metadata to the manifest models so the compiler validates node structures dynamically rather than hardcoding checks.
* **Clear Retry Attempt Semantics**:
  * `MaxAttempts` means the **total attempts including the first execution**. 
  * Example: `MaxAttempts = 3` represents **1 initial attempt + 2 retries**.
  * Track and store attempts using 1-indexed count: `AttemptNumber = 1, 2, 3` (not 0-indexed count).

---

## Proposed Changes

### 1. Update [NodePackageManifest.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/Knotarium.Core/NodePackageManifest.cs) [MODIFY]
Add `RetryPolicy` and `TriggerOnly` fields:
```csharp
public sealed record RetryPolicy(
    int MaxAttempts = 3,          // Inclusive total limit
    int InitialDelaySeconds = 2,
    double BackoffRate = 2.0,
    bool Jitter = true,
    int MaxDelaySeconds = 30
);

public sealed record NodePackageManifest(
    // existing fields...
    NodeSideEffectKind? SideEffectKind,
    RetryPolicy? RetryPolicy,
    bool TriggerOnly = false      // Metadata-driven constraint (prevents hardcoded checks)
);
```

### 2. Update Compiler Parsing in [WorkflowCompiler.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/Knotarium.Core/Compiler/WorkflowCompiler.cs) [MODIFY]
In the manifest validation pipeline:
* If the parsed manifest `SideEffectKind` is null or omitted, explicitly set it to `NodeSideEffectKind.NonIdempotentSideEffect` (default-deny).
* Populate default `RetryPolicy` if none is configured.

---

## Verification & Test Checklist

### 1. Unit Tests
* Write unit tests in `NodeManifestCompilerTests.cs` to verify:
  * **Default-Deny**: Parse a manifest lacking the `sideEffectKind` field. Assert that the compiled and validated `NodePackageManifest` assigns `NodeSideEffectKind.NonIdempotentSideEffect`.
  * **MaxAttempts Parsing**: Parse a manifest containing a custom `retryPolicy` and verify that all configuration fields are parsed correctly, aligning with 1-indexed total attempts.
  * **Trigger Metadata**: Validate that `TriggerOnly` compiles successfully as a boolean property.
