# Step 10 — B4.2: Schedule Fires Idempotency

## Goal
Implement database-backed schedule fire claiming to guarantee strict fire-level idempotency. By creating a `ScheduleFires` table with a composite `UNIQUE(ScheduleId, PlannedFireAtUtc)` constraint and status enums, we establish a robust transactional boundary. This ensures that even during a server restart, duplicate scheduling calls fail the database constraint and are cleanly rejected.

---

## Invariant Alignment
* **Invariant 4.3 (Fire-Level Idempotency):** The `UNIQUE(ScheduleId, PlannedFireAtUtc)` composite index rejects duplicate inserts, preventing double-firing under concurrency or worker delay.
* **Unified Transactional Claim Boundary**: To prevent time-drift gaps, the enqueuing claim, `ExecutionInstance` registration, and updating `Schedule.NextFireAtUtc` must execute within a single atomic transaction.
* **Exception Filtering**: SQLite constraint exceptions are explicitly filtered. We only catch unique index constraint violations as duplicate fires; all other general SQL exceptions are rethrown.

---

## Proposed Changes

### 1. Create [ScheduleFire.cs] and Status Enum [NEW]
Define status state enums:
```csharp
public enum ScheduleFireStatus
{
    Claimed,
    ExecutionCreated,
    Failed
}

public sealed class ScheduleFire
{
    public Guid Id { get; set; }
    public Guid ScheduleId { get; set; }
    public DateTime PlannedFireAtUtc { get; set; }
    public DateTime FiredAtUtc { get; set; }
    public Guid? ExecutionInstanceId { get; set; }
    public ScheduleFireStatus Status { get; set; }
}
```

### 2. Configure index in [AppDbContext.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/Knotarium.Infrastructure/Persistence/AppDbContext.cs) [MODIFY]
```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);

    // Enforce Invariant 4.3
    modelBuilder.Entity<ScheduleFire>()
        .HasIndex(sf => new { sf.ScheduleId, sf.PlannedFireAtUtc })
        .IsUnique();
}
```

### 3. Implement Transactional Claim Boundary [NEW]
Inside your schedule enqueuer service:
```csharp
public async Task<bool> ClaimAndEnqueueScheduleAsync(
    Guid scheduleId, 
    DateTime plannedFireAtUtc, 
    DateTime nextFireAtUtc, // Passed directly to prevent split transaction gaps
    CancellationToken ct)
{
    using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
    try
    {
        // 1. Insert ScheduleFire with status Claimed.
        // If a duplicate exists, this throws a Unique Constraint exception immediately.
        var fire = new ScheduleFire
        {
            Id = Guid.NewGuid(),
            ScheduleId = scheduleId,
            PlannedFireAtUtc = plannedFireAtUtc,
            FiredAtUtc = DateTime.UtcNow,
            Status = ScheduleFireStatus.Claimed
        };
        await _dbContext.ScheduleFires.AddAsync(fire, ct);
        await _dbContext.SaveChangesAsync(ct); // Force flush to check constraint

        // 2. Create and Enqueue ExecutionInstance
        var execution = CreateExecutionInstanceFromSchedule(scheduleId);
        await _dbContext.ExecutionInstances.AddAsync(execution, ct);

        // 3. Link execution and update status
        fire.ExecutionInstanceId = execution.Id;
        fire.Status = ScheduleFireStatus.ExecutionCreated;
        _dbContext.Entry(fire).State = EntityState.Modified;

        // 4. Update the schedule NextFireAtUtc in the SAME transaction (Must Fix)
        var schedule = await _dbContext.Schedules.FindAsync(new object[] { scheduleId }, ct);
        schedule.NextFireAtUtc = nextFireAtUtc;
        _dbContext.Entry(schedule).Property(s => s.NextFireAtUtc).IsModified = true;

        await _dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
    catch (DbUpdateException dbEx) when (IsUniqueConstraintViolation(dbEx)) // Filter SQLite constraints (Should Fix)
    {
        await transaction.RollbackAsync(ct);
        _logger.LogWarning($"Duplicate fire detected and rejected for schedule {scheduleId} at {plannedFireAtUtc}");
        return false; // Safely ignore duplicate enqueues
    }
    catch
    {
        await transaction.RollbackAsync(ct);
        throw; // Rethrow general failures (connection issues, invalid columns)
    }
}

private bool IsUniqueConstraintViolation(DbUpdateException ex)
{
    // Check if inner exception maps to SQLite unique constraint violation (error code 19 / SQLITE_CONSTRAINT_UNIQUE)
    return ex.InnerException != null && ex.InnerException.Message.Contains("UNIQUE constraint failed");
}
```

---

## Verification & Test Checklist

### 1. Integration Tests
* Write integration tests in `ScheduleFiresIdempotencyTests.cs` verifying:
  * **Transactional Boundary**: Call `ClaimAndEnqueueScheduleAsync` concurrently from multiple threads for the identical planned fire slot. Assert that exactly one call returns true (creating one execution instance) and the others throw/return false without enqueuing any redundant runs.
  * **SQLite Exception Filter**: Verify that a non-unique constraint exception (e.g. database disk full or network socket disconnect) correctly bubbles up and is rethrown rather than returning false.
  * **Claim Status Check**: Assert that upon successful completion, the `ScheduleFires` database record transitions from `Claimed` to `ExecutionCreated`.

### 2. Manual Verification
* Verify the SQLite database schema and constraints.
