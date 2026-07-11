# Step 10 — B3.4: Pre-Execution Markers

## Goal
Implement a precise write-ahead journaling boundary for non-idempotent node executions. By tracking unique execution `AttemptId` codes across both pre-execution markers and completion events, the recovery engine can accurately handle loop paths or manual retries. Both clean success and clean failure execution results must write matching `AttemptId` completion records, ensuring startup recovery scans only identify actual unresolved crash interruptions.

---

## Invariant Alignment
* **Invariant 6.1 (Crash-Mid-Non-Idempotent Boundary):** Pre-execution marker `"AttemptingExternalEffect"` is written immediately before calling a non-idempotent node. If a crash occurs, on recovery the node must never be re-run and never assumed successful; it transitions directly to `RequiresManualDecision`.
* **Precise Attempt Mapping**: Success (`NodeExecutionSucceeded`) and failure (`NodeExecutionFailed`) completion events must both write matching `AttemptId` payloads to prevent false-positive manual decision recovery flags.

---

## Proposed Changes

### 1. Update Pre-Execution and Completions in [WorkflowExecutor.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/KnotGarden.Features/Execution/WorkflowExecutor.cs) [MODIFY]
Directly before invoking `INodeExecutor.ExecuteAsync` for a non-idempotent node:
```csharp
var manifest = GetNodeManifest(node.Type);
if (manifest.SideEffectKind == NodeSideEffectKind.NonIdempotentSideEffect)
{
    var attemptId = Guid.NewGuid(); // Unique attempt tracking identifier
    
    // 1. Write pre-execution marker containing AttemptId
    var marker = new JournalEntry(
        Id: Guid.NewGuid(),
        ExecutionInstanceId: executionInstance.Id,
        EventType: "AttemptingExternalEffect",
        Payload: JsonSerializer.Serialize(new { 
            NodeId = node.Id, 
            AttemptId = attemptId,
            SideEffectKind = "NonIdempotentSideEffect",
            StartedAtUtc = DateTime.UtcNow
        }),
        PayloadVersion: "v2"
    );
    await _dbContext.JournalEntries.AddAsync(marker, cancellationToken);
    await _dbContext.SaveChangesAsync(cancellationToken); // Flush BEFORE invoking node code

    try
    {
        // 2. Execute node...
        var result = await executor.ExecuteAsync(input, context, cancellationToken);

        // 3. Write Success carrying matching AttemptId
        var completion = new JournalEntry(
            Id: Guid.NewGuid(),
            ExecutionInstanceId: executionInstance.Id,
            EventType: "NodeExecutionSucceeded",
            Payload: JsonSerializer.Serialize(new { 
                NodeId = node.Id, 
                AttemptId = attemptId, // Must match
                Output = result.Payload 
            }),
            PayloadVersion: "v2"
        );
        await _dbContext.JournalEntries.AddAsync(completion, cancellationToken);
    }
    catch (Exception ex)
    {
        // 4. Write Clean Failure carrying matching AttemptId (Must Fix — prevent false positives)
        var failure = new JournalEntry(
            Id: Guid.NewGuid(),
            ExecutionInstanceId: executionInstance.Id,
            EventType: "NodeExecutionFailed",
            Payload: JsonSerializer.Serialize(new { 
                NodeId = node.Id, 
                AttemptId = attemptId, // Must match
                Error = ex.Message 
            }),
            PayloadVersion: "v2"
        );
        await _dbContext.JournalEntries.AddAsync(failure, cancellationToken);
        throw; // Bubble up to standard retry/failure gate
    }
}
```

### 2. Update [RecoveryService.cs] Startup Check [NEW]
On system startup:
* Fetch active execution instances and scan their journals.
* For each instance:
  * For each `AttemptingExternalEffect` journal entry:
    * Extract `NodeId` and `AttemptId`.
    * Search the journal for a corresponding `NodeExecutionSucceeded` or `NodeExecutionFailed` containing **the identical `AttemptId`**.
    * **Enforce Crash Recovery**: If no completion event with that exact `AttemptId` is found, the system crashed during the non-idempotent call. **Never re-run the node**. Update `NodeExecutionStatus` of the node to `RequiresManualDecision` and update execution status to `Suspended`.

---

## Verification & Test Checklist

### 1. Integration Tests
* Write integration tests in `CrashRecoveryTests.cs` verifying:
  * **No False Positives**: Execute a non-idempotent node that cleanly throws an exception. Assert that `NodeExecutionFailed` is written to the journal carrying the matching `AttemptId`, and that rebooting/running the startup `RecoveryService` does **not** flag this as an uncompleted crash (since a matching completion event exists).
  * **AttemptId Precision**: Assert that a crashed `AttemptId` (marker written but no success/failure completes) transitions the node to `RequiresManualDecision` on reboot.

### 2. Manual Verification
* Kill the host process while a non-idempotent node is executing, restart, and confirm the manual decision warning displays.
