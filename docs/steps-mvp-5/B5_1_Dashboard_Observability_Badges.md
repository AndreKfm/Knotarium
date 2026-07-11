# Step 16 — B5.1: Observability Badges UI & Decision API

## Goal
Design and integrate premium, glassmorphic status badge visualizations into the frontend workflow execution view and canvas node layers. The UI must map new backend execution states and node statuses dynamically. Additionally, we are implementing the backend manual decision API endpoint (`POST /manual-decision`) to allow operators to manually override, skip, or retry failed non-idempotent nodes.

---

## Invariant Alignment
* **Run-Level status visualizer**: Maps run-wide `Suspended` to `"Waiting"`, `WaitingForRetry` to `"Retrying"`, and `Cancelled` to `"Cancelled"`.
* **Node-Level status visualizer**: Displays `Retrying` or `RequiresManualDecision` directly on the nodes.
* **Manual Decision Boundary**: Operators can resolve `RequiresManualDecision` states. The decision action is transactionally executed and logged as `ManualDecisionRecorded` in the canonical `ExecutionJournal`.

---

## Proposed Changes

### 1. Update/Add Status Styles in [index.css](file:///d:/Private/Source/AknSideProjects/Automate/Frontend/src/index.css) [MODIFY]
Add glassmorphic classes and keyframes animations:
```css
.status-badge-waiting-retry {
  background: hsla(200, 92%, 50%, 0.15);
  color: hsl(200, 92%, 60%);
  border-color: hsla(200, 92%, 50%, 0.3);
}

.status-badge-manual {
  background: hsla(0, 92%, 50%, 0.15);
  color: hsl(0, 92%, 60%);
  border-color: hsla(0, 92%, 50%, 0.3);
  box-shadow: 0 0 10px hsla(0, 92%, 50%, 0.2);
}
```

### 2. Implement Backend manual decision endpoint in [ExecutionsController.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/KnotGarden.Api/Controllers/ExecutionsController.cs) [NEW]
Add the POST routing action:
```csharp
[HttpPost("{id}/nodes/{nodeId}/manual-decision")]
public async Task<IActionResult> MakeManualDecisionAsync(
    Guid id,
    string nodeId,
    [FromBody] ManualDecisionRequest request,
    CancellationToken ct)
{
    var success = await _workflowExecutor.ApplyManualDecisionAsync(
        id,
        nodeId,
        request.Decision, // "Retry" | "Skip" | "Fail"
        request.Reason,
        request.ExpectedAttemptId,
        ct
    );

    return success ? Ok(new { Message = "Decision recorded successfully." }) : BadRequest("Failed to apply manual decision.");
}
```
* Every applied manual decision must append a `ManualDecisionRecorded` entry in the `ExecutionJournal` to ensure audit completeness.

---

## Verification & Test Checklist

### 1. Integration Tests
* Write integration tests in `ManualDecisionTests.cs` verifying:
  * **Halt and Override**: Trigger a non-idempotent node failure. Confirm it transitions to `RequiresManualDecision`. POST a manual decision of `"Skip"` and assert that the workflow engine resumes, skips that node, writes `ManualDecisionRecorded` in the journal, and completes the workflow downstream.
  * **Invalid Attempt Id**: Send a manual decision with a stale `ExpectedAttemptId`. Assert that the request is rejected.

### 2. Manual Verification
* Force a credit card charge node (non-idempotent) to fail. Open the visualizer, click the "Skip Node" button, and verify the run completes.
