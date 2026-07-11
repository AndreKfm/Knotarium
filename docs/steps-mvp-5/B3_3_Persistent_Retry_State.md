# Step 9 — B3.3: Persistent Retry State

## Goal
Implement the SQLite database schema and persistence logic for tracking active retry states (`NodeRetryStates`). To ensure reliability and data safety, the table must enforce a composite uniqueness constraint, execute structured lifecycle cleanups, and strictly sanitize stored error logs to prevent credential leakage.

---

## Invariant Alignment
* **Invariant 5.3 (Persistent Retry State):** Retry attempt states survive unexpected restarts by storing them in a dedicated SQLite table.
* **Uniqueness Constraints**: The database configuration enforces a strict **`UNIQUE(ExecutionInstanceId, NodeId)`** index.
* **Lifecycle Cleanup Rules**:
  * **Create/Update**: Record is initialized or updated when a node retry is scheduled (Step 8 / B3.2 transaction).
  * **Delete on Success**: If the retry succeeds, the record is immediately deleted from SQLite.
  * **Clean/Exhaust on Final Failure**: If attempts exceed `MaxAttempts`, the record is removed or marked exhausted to prevent table bloating.
* **Security & Sanitization**: Do not write full un-scrubbed exception detail strings to the database to prevent accidental leakage of connection strings, HTTP auth headers, or dynamic secrets. Scrub credentials prior to logging.

---

## Proposed Changes

### 1. Create [NodeRetryState.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/KnotGarden.Core/NodeRetryState.cs) [NEW]
Define the schema tracker:
```csharp
public sealed class NodeRetryState
{
    public Guid Id { get; set; }
    public Guid ExecutionInstanceId { get; set; }
    public string NodeId { get; set; } = null!;
    public int AttemptNumber { get; set; }
    public DateTime NextRetryAtUtc { get; set; }
    public string SanitizedFailureMessage { get; set; } = null!; // Scrubbed of secrets
}
```

### 2. Configure Index in [AppDbContext.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/KnotGarden.Infrastructure/Persistence/AppDbContext.cs) [MODIFY]
```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);

    // Enforce Uniqueness
    modelBuilder.Entity<NodeRetryState>()
        .HasIndex(r => new { r.ExecutionInstanceId, r.NodeId })
        .IsUnique();
}
```
* Register the `DbSet<NodeRetryState> NodeRetryStates` property and generate EF migrations.

### 3. Implement Lifecycle in [WorkflowExecutor.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/KnotGarden.Features/Execution/WorkflowExecutor.cs) [MODIFY]
* **On Scheduling Retry**: Handled transactionally in Step 8 (upserting retry state and work items atomically).
* **On Node Success**: If a retried node completes successfully, run `_dbContext.NodeRetryStates.Remove(retryState)` to delete the record in the same transaction.
* **On Final Failure**: Delete the `NodeRetryState` and append `NodeExecutionFailed` to the canonical journal.

---

## Verification & Test Checklist

### 1. Integration Tests
* Write integration tests in `RetryPersistenceTests.cs` verifying:
  * **Composite Index**: Attempt to save two `NodeRetryState` rows for the same `ExecutionInstanceId` and `NodeId`. Assert the second write fails with a unique constraint violation exception.
  * **Lifecycle Cleanup**: Trace a failing node that eventually succeeds on attempt 2. Verify that the `NodeRetryStates` table contains a row during the backoff phase, but is completely empty (record successfully deleted) once the workflow finishes.
  * **Credential Scrubbing**: Inject an exception containing an `"Authorization: Bearer secret_key_123"` header. Verify that `SanitizedFailureMessage` in the database does *not* contain the substring `"secret_key_123"`.

### 2. Manual Verification
* Inspect the database after a successful workflow run and confirm `NodeRetryStates` contains no leftover rows.
