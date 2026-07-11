# Step 12 — B4.3: Scheduling Worker Service

## Goal
Implement the timezone-aware background `SchedulingWorker` hosted service (`IHostedService`) in the API project. To maintain a clean architecture (resolving layer splits), the Api worker must remain a **thin polling loop**, delegating all business logic to `ScheduleEvaluationService.cs` under the Features layer. The service evaluates active cron occurrences using timezone-aware `DateTimeOffset` offsets, handles DST transitions, enqueues executions, advances schedules on duplicate rejections, and caps catch-up loops to prevent starvation.

---

## Invariant Alignment
* **Layer Decoupling (F4 Split):** `Knotarium.Api` hosts the thin polling service (`SchedulingWorker`), which delegates evaluation and database writes to `ScheduleEvaluationService` inside `Knotarium.Features`.
* **Stuck-Loop Liveness Fix**: If enqueuing returns `false` (duplicate index constraint rejected), we **must still advance `NextFireAtUtc`** to prevent the schedule from executing and hitting the unique constraint in an infinite stuck-loop every tick.
* **DST-Safe Evaluation**: Using `DateTimeOffset` directly with timezone mapping resolves DST ambiguity local hours.
* **Capped Missed-Window Loop**: The `FireOnceForMissedWindow` catch-up loop is bounded to a maximum of **10 catch-ups** to prevent worker starvation after long downtime.

---

## Proposed Changes

### 1. Register Hosted Service in [Program.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/Knotarium.Api/Program.cs) [MODIFY]
```csharp
builder.Services.AddHostedService<SchedulingWorker>();
```

### 2. Implement thin Hosted Service in [SchedulingWorker.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/Knotarium.Api/Services/SchedulingWorker.cs) [NEW]
The Hosted Service is a thin polling loop:
```csharp
public class SchedulingWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SchedulingWorker> _logger;

    public SchedulingWorker(IServiceProvider serviceProvider, ILogger<SchedulingWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var evaluator = scope.ServiceProvider.GetRequiredService<IScheduleEvaluationService>();
                await evaluator.EvaluateActiveSchedulesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduling worker encountered an unhandled exception.");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
```

### 3. Implement Business Logic in [ScheduleEvaluationService.cs](file:///d:/Private/Source/AknSideProjects/Automate/Backend/Knotarium.Features/Schedules/ScheduleEvaluationService.cs) [NEW]
All scheduling evaluation and enqueuing policies live in Features:
```csharp
using Cronos;

public class ScheduleEvaluationService : IScheduleEvaluationService
{
    private readonly AppDbContext _dbContext;
    private readonly IWorkflowEnqueueService _enqueuer;
    private readonly ILogger<ScheduleEvaluationService> _logger;

    public async Task EvaluateActiveSchedulesAsync(CancellationToken ct)
    {
        var activeSchedules = await _dbContext.Schedules
            .Where(s => s.NextFireAtUtc <= DateTime.UtcNow)
            .ToListAsync(ct);

        foreach (var schedule in activeSchedules)
        {
            // Exception Isolation: One bad timezone or cron doesn't crash the worker thread
            try
            {
                await ProcessSingleScheduleAsync(schedule, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed processing schedule {schedule.Id}. Isolation preserved thread.");
            }
        }
    }

    private async Task ProcessSingleScheduleAsync(Schedule schedule, CancellationToken ct)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        var plannedFire = schedule.NextFireAtUtc;

        // 1. Next Occurrence calculation using Cronos & DateTimeOffset (prevents skipped slots)
        var cron = CronExpression.Parse(schedule.CronExpression);
        var plannedOffset = new DateTimeOffset(plannedFire, TimeSpan.Zero);
        var nextOccurrence = cron.GetNextOccurrence(plannedOffset, tz);

        if (!nextOccurrence.HasValue) return;
        var nextUtc = nextOccurrence.Value.UtcDateTime;

        // 2. Enforce FireOnceForMissedWindow bounded policy (Must Fix)
        if (nextUtc <= DateTime.UtcNow)
            _logger.LogWarning($"Schedule {schedule.Id} missed slots. Firing once for the latest missed slot.");
            
            var latestMissed = cron.GetNextOccurrence(plannedOffset, tz);
            int catchUpCount = 0;
            // Cap loop at 10 to prevent infinite catch-up loops (Must Fix)
            while (latestMissed.HasValue && latestMissed.Value.UtcDateTime <= DateTime.UtcNow && catchUpCount < 10)
            {
                plannedFire = latestMissed.Value.UtcDateTime;
                latestMissed = cron.GetNextOccurrence(latestMissed.Value, tz);
                catchUpCount++;
            }
            
            if (latestMissed.HasValue)
            {
                nextUtc = latestMissed.Value.UtcDateTime;
            }
        }

        // 3. Transactional Claim & Enqueue updating NextFireAtUtc in ONE transaction (B4.2)
        var claimed = await _enqueuer.ClaimAndEnqueueScheduleAsync(schedule.Id, plannedFire, nextUtc, ct);
        
        // 4. Stuck-Loop Liveness Fix: If claim fails (duplicate rejected), we MUST still advance schedule NextFireAtUtc
        if (!claimed)
        {
            _logger.LogWarning($"Schedule {schedule.Id} duplicate fire rejected for planned slot {plannedFire}. Advancing NextFireAtUtc to prevent stuck loops.");
            schedule.NextFireAtUtc = nextUtc;
            _dbContext.Entry(schedule).Property(s => s.NextFireAtUtc).IsModified = true;
            await _dbContext.SaveChangesAsync(ct);
        }
    }
}
```

---

## Verification & Test Checklist

### 1. Integration Tests
* Write integration tests in `SchedulingWorkerTests.cs` verifying:
  * **Liveness Stuck-Loop Fix**: Simulate a duplicate enqueuing rejection (so `ClaimAndEnqueueScheduleAsync` returns false). Assert that the scheduler successfully updates `NextFireAtUtc` in the database to the upcoming future slot, and does **not** get stuck in an infinite loop evaluating the same due fire slot on subsequent loop ticks.
  * **DST Ambiguity Check**: Configure Berlin daily clock transitions and assert computed occurrences resolve cleanly.
  * **Loop Boundary Capping**: Mock a schedule whose `NextFireAtUtc` is 3 days in the past with a 1-minute cron interval. Assert that the evaluation loop completes and does not exceed the maximum cap of 10 catch-ups.
