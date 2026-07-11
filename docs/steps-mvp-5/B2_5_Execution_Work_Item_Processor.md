# Step 7 — B2.5: Execution Work Item Processor

## Goal
Implement the background queue processor for enqueued `ExecutionWorkItem` rows. To preserve Knotarium's **single-writer concurrency guarantee**, the existing Hosted Service is expanded to poll both execution instances and the `ExecutionWorkItem` table under a shared execution lock. When reclaiming a `Resume` work item, the engine folds the journal to rehydrate the variable bag, sets the waiting node's output, and dispatches traversals **strictly starting from its outgoing edges, completely bypassing completed nodes**.

---

## Invariant Alignment
* **Single-Writer Guarantee**: To prevent race conditions or database locks, work items are drained under the same single-process lock/guard as standard active execution runs.
* **Atomic Claim**: Workers claim pending items using database-level `ExecuteUpdateAsync` where `Status == Pending && (NotBeforeUtc == null || NotBeforeUtc <= now)`.
* **Invariant 3.3 (Resume Traversal Precision):** Traversal continues downstream starting strictly from the resumed node's outgoing connections. Bypassing and skipping already succeeded nodes is fully enforced.
* **Rehydration folds**: Rehydrate memory bags via the `JournalFoldService` from Step 4.

---

## Proposed Changes

### 1. Integrate with the Background Worker Loop [MODIFY]
In the background execution Hosted Service (e.g. `ExecutionWorker.cs` or polling thread):
* Update the main loop to periodically query and claim pending work items:
```csharp
private async Task PollAndProcessWorkItemsAsync(CancellationToken ct)
{
    using var scope = _serviceProvider.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // 1. Query pending work items due for execution
    var workItems = await db.ExecutionWorkItems
        .Where(w => w.Status == WorkItemStatus.Pending && 
                   (w.NotBeforeUtc == null || w.NotBeforeUtc <= DateTime.UtcNow))
        .OrderBy(w => w.CreatedAtUtc)
        .Take(5)
        .ToListAsync(ct);

    foreach (var item in workItems)
    {
        // 2. Atomic claim at SQL level to prevent concurrency races
        var claimed = await db.ExecutionWorkItems
            .Where(w => w.Id == item.Id && w.Status == WorkItemStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, WorkItemStatus.Running), ct);

        if (claimed == 0) continue; // Claimed by another thread

        // 3. Process according to type
        try
        {
            await ProcessWorkItemAsync(item, db, ct);
            
            item.Status = WorkItemStatus.Completed;
            item.ProcessedAtUtc = DateTime.UtcNow;
            db.Entry(item).State = EntityState.Modified;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            item.Status = WorkItemStatus.Failed;
            db.Entry(item).State = EntityState.Modified;
            await db.SaveChangesAsync(ct);
            _logger.LogError(ex, $"Failed to process execution work item {item.Id}");
        }
    }
}
```

### 2. Implement Precision Traversal Rehydration [MODIFY]
Inside `WorkflowExecutor.cs` (processing the claimed `"Resume"` work item):
```csharp
public async Task ExecuteResumeWorkItemAsync(Guid instanceId, string suspendedNodeId, JsonElement callbackPayload, CancellationToken ct)
{
    // 1. Retrieve Canonical Journal History
    var journalEntries = await _dbContext.JournalEntries
        .Where(j => j.ExecutionInstanceId == instanceId)
        .ToListAsync(ct);

    // 2. Fold Journal to Reconstruct variables and check status (B2.2 / Step 4)
    var folder = new JournalFoldService();
    var (variables, currentStatus) = folder.FoldJournal(journalEntries);

    // 3. Build a set of node IDs that already completed successfully
    var succeededNodeIds = journalEntries
        .Where(j => j.EventType == "NodeExecutionSucceeded")
        .Select(j => ExtractNodeIdFromPayload(j.Payload))
        .ToHashSet();

    // 4. Bind the callback output payload to the suspended node
    variables[suspendedNodeId + ".output"] = callbackPayload;

    // 5. precision traversal: get outgoing edges connected to the suspended node
    var workflow = await LoadWorkflowDefinitionByInstanceAsync(instanceId, ct);
    var outgoingEdges = workflow.Edges.Where(e => e.SourceNodeId == suspendedNodeId).ToList();

    // 6. Traverse downstream, completely skipping succeeded node execution paths (Must Fix - B2.5)
    foreach (var edge in outgoingEdges)
    {
        if (succeededNodeIds.Contains(edge.TargetNodeId))
        {
            _logger.LogInformation($"Node {edge.TargetNodeId} already succeeded in previous attempt. Bypassing execution.");
            continue; // STRICT BYPASS
        }

        await ExecuteNodeSubtreeAsync(instanceId, edge.TargetNodeId, variables, succeededNodeIds, ct);
    }
}
```

---

## Verification & Test Checklist

### 1. Integration Tests
* Write integration tests in `WorkItemProcessorTests.cs` verifying:
  * **Precision Traversal Bypass**: Pre-populate an execution instance where `Node-A` (HTTP GET) and `Node-B` (Webhook Wait) are registered, and `Node-A` is mapped as `Succeeded` in the journal. Resume the webhook, and assert that the traversal engine successfully executes downstream nodes connected to `Node-B` without invoking `Node-A` again.
  * **Atomic claim Concurrency**: Concurrently instantiate 3 hosted polling worker threads. Verify that exactly one processes the work item and others bypass safely.

### 2. Manual Verification
* Trigger a webhook pause, inspect the `ExecutionWorkItems` table status transitions from `Pending` -> `Running` -> `Completed` during worker evaluation.
