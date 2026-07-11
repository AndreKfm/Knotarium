# Step 8 — B3.2: Gated Auto-Retry Traversal

## Goal
Implement the core engine failure and retry traversal loop inside `WorkflowExecutor.cs`. When a node execution fails, the engine must evaluate its `SideEffectKind`. If eligible for auto-retries, the execution instance transitions to `ExecutionStatus.WaitingForRetry` and atomically enqueues a durable background retry work item while upserting the persistent `NodeRetryState` tracking record.

---

## Invariant Alignment
* **Invariant 5.1 (Idempotency-Gated Retries):** Auto-retries are strictly gated by `NodeSideEffectKind`. Non-idempotent node failures route to `RequiresManualDecision`.
* **Precise Observability State**: When waiting for a retry backoff delay, the run status updates to `ExecutionStatus.WaitingForRetry` (completely distinct from event-waiting `Suspended` states).
* **Unified Retry Transaction (Must Fix)**: To guarantee attempt counts and schedules survive crashes, the engine must atomically **enqueue the `Retry` `ExecutionWorkItem` AND upsert the `NodeRetryState` record** inside a single database transaction. 
* **1-Indexed Attempt Count**: `GetAttemptNumber` queries `NodeRetryState.AttemptNumber` (defaulting to `1` if no record exists yet).

---

## Proposed Changes

### 1. Implement Retry Backoff Calculator [NEW]
```csharp
public static class RetryBackoffCalculator
{
    public static TimeSpan CalculateDelay(RetryPolicy policy, int attemptNumber)
    {
        // attemptNumber is 1-indexed (1 is initial attempt, so retries start at 2)
        int retryCount = attemptNumber - 1; 
        if (retryCount <= 0) return TimeSpan.Zero;

        double delaySeconds = policy.InitialDelaySeconds * Math.Pow(policy.BackoffRate, retryCount);
        if (policy.Jitter)
        {
            // Apply +-15% random jitter via Random.Shared
            double jitterAmount = delaySeconds * 0.15 * (Random.Shared.NextDouble() * 2.0 - 1.0);
            delaySeconds += jitterAmount;
        }

        delaySeconds = Math.Min(delaySeconds, policy.MaxDelaySeconds);
        return TimeSpan.FromSeconds(Math.Max(delaySeconds, 0));
    }
}
```

### 2. Update failure handler in [WorkflowExecutor.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/Knotarium.Features/Execution/WorkflowExecutor.cs) [MODIFY]
```csharp
var manifest = GetNodeManifest(node.Type);
if (manifest.SideEffectKind == NodeSideEffectKind.NonIdempotentSideEffect)
{
    await RouteToManualDecisionAsync(executionInstance, node, exception, cancellationToken);
    return;
}

// 1-indexed attempt lookup
var attemptNumber = await GetAttemptNumberAsync(executionInstance.Id, node.Id, cancellationToken);
if (attemptNumber < manifest.RetryPolicy.MaxAttempts)
{
    var nextAttempt = attemptNumber + 1;
    var delay = RetryBackoffCalculator.CalculateDelay(manifest.RetryPolicy, nextAttempt);
    var nextRetryAtUtc = DateTime.UtcNow.Add(delay);

    using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
    try
    {
        // 1. Update run status cache to WaitingForRetry (Distinct from Suspended)
        executionInstance.Status = ExecutionStatus.WaitingForRetry;
        _dbContext.Entry(executionInstance).Property(e => e.Status).IsModified = true;

        // 2. Enqueue durable background execution retry work item (Must Fix - B3.2)
        var workItem = new ExecutionWorkItem
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = executionInstance.Id,
            Type = "Retry",
            Payload = JsonSerializer.Serialize(new { 
                NodeId = node.Id, 
                AttemptNumber = nextAttempt
            }),
            NotBeforeUtc = nextRetryAtUtc, // Scheduled backoff time (efficient querying column!)
            Status = WorkItemStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow
        };
        await _dbContext.ExecutionWorkItems.AddAsync(workItem, cancellationToken);

        // 3. Upsert NodeRetryState record in the SAME transaction (Must Fix - §4 compose retry)
        var retryState = await _dbContext.NodeRetryStates
            .SingleOrDefaultAsync(r => r.ExecutionInstanceId == executionInstance.Id && r.NodeId == node.Id, cancellationToken);

        if (retryState == null)
        {
            retryState = new NodeRetryState
            {
                Id = Guid.NewGuid(),
                ExecutionInstanceId = executionInstance.Id,
                NodeId = node.Id,
                AttemptNumber = nextAttempt,
                NextRetryAtUtc = nextRetryAtUtc,
                SanitizedFailureMessage = ScrubSecrets(exception.Message)
            };
            await _dbContext.NodeRetryStates.AddAsync(retryState, cancellationToken);
        }
        else
        {
            retryState.AttemptNumber = nextAttempt;
            retryState.NextRetryAtUtc = nextRetryAtUtc;
            retryState.SanitizedFailureMessage = ScrubSecrets(exception.Message);
            _dbContext.Entry(retryState).State = EntityState.Modified;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    catch
    {
        await transaction.RollbackAsync(cancellationToken);
        throw;
    }
}
else
{
    await HandleFatalNodeFailureAsync(executionInstance, node, exception, cancellationToken);
}
```

---

## Verification & Test Checklist

### 1. Integration Tests
* Write integration tests in `WorkflowExecutorRetryTests.cs` verifying:
  * **Unified Transactional Retry**: Simulate a crash mid-retry. Assert that *both* the `NodeRetryState` record and the `ExecutionWorkItem` queue are either successfully written together or cleanly rolled back, ensuring they never drift.
  * **WaitingForRetry Verification**: Assert that a failing `Pure` node transitions the overall `ExecutionInstance.Status` to `ExecutionStatus.WaitingForRetry`.
  * **Backoff Calculation Jitter**: Unit test `RetryBackoffCalculator` with different attempt values, asserting that `Random.Shared` calculations cap delays correctly at `MaxDelaySeconds`.

### 2. Manual Verification
* Trigger a failing idempotent HTTP node and verify database status switches to `WaitingForRetry`.
