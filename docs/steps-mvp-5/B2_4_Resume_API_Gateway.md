# Step 6 — B2.4: Resume API Gateway

## Goal
Implement the REST API resume gateway endpoint `/api/executions/resume` to process callback invocations. To ensure the resume flow is resilient from crash gaps using durable work items, we are introducing **atomic SQL-level token claiming** (binding the token strictly from `X-KnotGarden-Token` header/body to prevent query-string security anti-patterns), loading the original **immutable workflow definition version** upon resume, and enqueuing a durable **`ExecutionWorkItem`** to handle post-commit execution background traversals safely.

---

## Invariant Alignment
* **Atomic SQL-Level Token Claim**: Using raw SQL or EF Core `ExecuteUpdateAsync` to claim a correlation token atomically at the database engine level, preventing concurrency race conditions.
* **Token Binding**: The API controller binds the correlation token from the **`X-KnotGarden-Token` header or request body**, completely avoiding query-string security anti-patterns.
* **durable ExecutionWorkItem (Close Crash Gaps)**: Instead of dispatching workflow traversal via ephemeral in-memory post-commit `Task.Run` loops (which reintroduce execution crash gaps), we write a durable background `ExecutionWorkItem` of type `Resume` inside the same transaction. A background Hosted Service picks up and processes these work items.
* **Immutable Definition Boundary**: Suspended runs resume against the exact workflow definition version they started with. The `ExecutionInstance` loads its original `WorkflowDefinitionVersionId` from the database.

---

## Proposed Changes

### 1. Create [ExecutionWorkItem.cs] and Status Enum [NEW]
Define background work queue tracking tables:
```csharp
public enum WorkItemStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

public sealed class ExecutionWorkItem
{
    public Guid Id { get; set; }
    public Guid ExecutionInstanceId { get; set; }
    public string Type { get; set; } = null!; // "Resume" or "Retry"
    public string Payload { get; set; } = null!; // JSON context
    public DateTime? NotBeforeUtc { get; set; } // High-performance DB-level querying column
    public WorkItemStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
}
```
* Register this new DbSet in `AppDbContext.cs` and generate the EF Core database migration.

### 2. Implement Controller POST Endpoint in [ExecutionsController.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/KnotGarden.Api/Controllers/ExecutionsController.cs) [MODIFY]
```csharp
public record ResumeRequest(string? Token, JsonElement Payload);

[HttpPost("resume")]
public async Task<IActionResult> ResumeExecutionAsync(
    [FromBody] ResumeRequest request, 
    [FromHeader(Name = "X-KnotGarden-Token")] string? headerToken, // Secure header binding
    CancellationToken ct)
{
    var tokenValue = headerToken ?? request.Token;
    if (string.IsNullOrWhiteSpace(tokenValue))
    {
        return BadRequest("Correlation token is required.");
    }

    // Call feature layer to resume transactionally
    var success = await _workflowExecutor.ResumeWorkflowTransactionAsync(
        tokenValue,
        request.Payload,
        ct
    );

    return success ? Ok(new { Message = "Workflow resume request registered." }) : BadRequest("Failed to resume execution.");
}
```

### 3. Implement Transactional Resume in [WorkflowExecutor.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/KnotGarden.Features/Execution/WorkflowExecutor.cs) [MODIFY]
Wrap SQL token claim, version loading, and work item enqueuing in a single transaction:
```csharp
public async Task<bool> ResumeWorkflowTransactionAsync(string rawToken, JsonElement payload, CancellationToken ct)
{
    using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
    try
    {
        // 1. Atomic SQL-level Token Claim using EF Core ExecuteUpdateAsync
        var hashed = HashToken(rawToken);
        var affected = await _dbContext.CorrelationTokens
            .Where(t => t.HashedToken == hashed && t.ConsumedAtUtc == null && t.ExpiresAtUtc > DateTime.UtcNow)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAtUtc, DateTime.UtcNow), ct);

        if (affected == 0)
        {
            return false; // Already consumed, expired, or invalid
        }

        // Retrieve token details for execution context
        var token = await _dbContext.CorrelationTokens.SingleAsync(t => t.HashedToken == hashed, ct);

        // 2. Load ExecutionInstance
        var instance = await _dbContext.ExecutionInstances.FindAsync(new object[] { token.ExecutionInstanceId }, ct);
        
        // 3. Load exact immutable definition version that this instance started with (Must Fix)
        var definitionVersion = await _dbContext.WorkflowDefinitionVersions
            .SingleOrDefaultAsync(v => v.Id == instance.WorkflowDefinitionVersionId, ct);
            
        if (definitionVersion is null)
        {
            throw new InvalidOperationException("Cannot resume; original starting workflow definition version is missing from the database.");
        }

        // 4. Update overall run status cache projection
        instance.Status = ExecutionStatus.Running;
        _dbContext.Entry(instance).Property(e => e.Status).IsModified = true;

        // 5. Append WorkflowResumed to canonical journal
        var journal = new JournalEntry(
            Id: Guid.NewGuid(),
            ExecutionInstanceId: instance.Id,
            EventType: "WorkflowResumed",
            Payload: JsonSerializer.Serialize(new { NodeId = token.NodeId, Output = payload }),
            PayloadVersion: "v2"
        );
        await _dbContext.JournalEntries.AddAsync(journal, ct);

        // 6. Enqueue durable background execution work item
        var workItem = new ExecutionWorkItem
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = instance.Id,
            Type = "Resume",
            Payload = JsonSerializer.Serialize(new { NodeId = token.NodeId, Output = payload }),
            NotBeforeUtc = null, // Execute immediately
            Status = WorkItemStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };
        await _dbContext.ExecutionWorkItems.AddAsync(workItem, ct);

        await _dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return true;
    }
    catch
    {
        await transaction.RollbackAsync(ct);
        throw;
    }
}
```

---

## Verification & Test Checklist

### 1. Integration Tests
* Write integration tests in `ResumeIntegrationTests.cs` verifying:
  * **Crash Resiliency**: Trigger a resume API call, intercepting database execution immediately *after* the transaction commits, then forcefully abort the process. Restart, and verify that the background worker service automatically picks up the durable `Pending` `ExecutionWorkItem` and completes workflow traversal successfully.
  * **Atomic Concurrency Check**: Concurrently fire 5 duplicate HTTP resume callback requests. Assert that exactly one updates the token (affected rows = 1) and schedules a work item.
  * **Immutable Version Check**: Modify the active version of a workflow definition. Trigger a resume for an older suspended run, and assert the workflow **resumes and runs successfully against its original starting definition version**, successfully ignoring the newer active version.
  * **Secure Token Extraction**: Mock requests with X-KnotGarden-Token headers and request body. Assert both bind correctly, and query-string parameter resume requests are rejected.
