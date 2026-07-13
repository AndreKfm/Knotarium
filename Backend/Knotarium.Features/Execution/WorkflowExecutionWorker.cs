using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Features.Execution;

public class WorkflowExecutionWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private const int MaxWorkItemsPerCycle = 5;

    private readonly WorkflowExecutionQueue _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WorkflowExecutionWorker> _logger;
    private readonly string _workerId;
    // Run-level concurrency cap. Default 1 = fully serial (the historical, single-writer-safe behavior).
    // Each run already executes in its own DI scope (own WorkflowExecutor + AppDbContext), so a higher cap
    // is safe against shared-state races; SQLite remains single-writer, so concurrent runs' writes serialize
    // (WAL + busy_timeout) rather than corrupt. Raise Execution:MaxConcurrentRuns only if runs are I/O-bound.
    private readonly int _maxConcurrentRuns;
    private readonly SemaphoreSlim? _runSlots;

    public WorkflowExecutionWorker(
        WorkflowExecutionQueue queue,
        IServiceProvider serviceProvider,
        ILogger<WorkflowExecutionWorker> logger,
        IConfiguration? configuration = null)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _workerId = Guid.NewGuid().ToString();
        _maxConcurrentRuns = Math.Max(1, configuration?.GetValue("Execution:MaxConcurrentRuns", 1) ?? 1);
        _runSlots = _maxConcurrentRuns > 1 ? new SemaphoreSlim(_maxConcurrentRuns, _maxConcurrentRuns) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Workflow Execution Worker started with ID: {WorkerId}", _workerId);

        using (var recoveryScope = _serviceProvider.CreateScope())
        {
            var recoveryService = recoveryScope.ServiceProvider.GetRequiredService<RecoveryService>();
            var recoveredExecutions = await recoveryService.RecoverIncompleteExternalEffectsAsync(stoppingToken);
            if (recoveredExecutions > 0)
            {
                _logger.LogInformation("Recovered {RecoveredExecutions} interrupted executions before polling started.", recoveredExecutions);
            }
        }

        // 1. Startup Guard
        using (var startupScope = _serviceProvider.CreateScope())
        {
            var dbContext = startupScope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Ensure schema exists or migrate before checking
            await dbContext.Database.EnsureCreatedAsync(stoppingToken);

            var threshold = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
            var activeWorkerExists = await dbContext.ActiveWorkers.AnyAsync(w => w.LastHeartbeat > threshold, stoppingToken);
            if (activeWorkerExists)
            {
                _logger.LogCritical("Another active executor worker is already running for this database instance. Aborting startup.");
                throw new InvalidOperationException("Another active executor worker is already running for this database instance.");
            }

            // Clean up any stale sessions and register this worker
            var staleWorkers = await dbContext.ActiveWorkers.ToListAsync(stoppingToken);
            if (staleWorkers.Any())
            {
                dbContext.ActiveWorkers.RemoveRange(staleWorkers);
            }

            var me = new ActiveWorker { Id = _workerId, LastHeartbeat = DateTimeOffset.UtcNow };
            dbContext.ActiveWorkers.Add(me);
            await dbContext.SaveChangesAsync(stoppingToken);
        }

        // 1b. Crash recovery, now that the startup guard has confirmed we are the sole worker: fail runs left
        // orphaned in Running, reclaim work items stuck in Running, and re-queue runs that were Pending (the
        // in-memory queue does not survive a restart). Without this, orphaned runs sit forever and Pending
        // runs never execute.
        using (var recoveryScope = _serviceProvider.CreateScope())
        {
            var recoveryService = recoveryScope.ServiceProvider.GetRequiredService<RecoveryService>();
            try
            {
                var failedOrphans = await recoveryService.FailOrphanedRunningRunsAsync(stoppingToken);
                var reclaimedWorkItems = await recoveryService.ReclaimStuckWorkItemsAsync(stoppingToken);
                var pendingRunIds = await recoveryService.GetPendingRunIdsAsync(stoppingToken);
                foreach (var pendingRunId in pendingRunIds)
                {
                    _queue.QueueExecution(pendingRunId);
                }

                if (failedOrphans > 0 || reclaimedWorkItems > 0 || pendingRunIds.Count > 0)
                {
                    _logger.LogInformation(
                        "Crash recovery: failed {Orphans} orphaned run(s), reclaimed {WorkItems} stuck work item(s), re-queued {Pending} pending run(s).",
                        failedOrphans, reclaimedWorkItems, pendingRunIds.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Crash recovery step failed during worker startup.");
            }
        }

        // 2. Start Background Heartbeat Loop Task
        var heartbeatTask = Task.Run(async () =>
        {
            using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await heartbeatTimer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested)
            {
                try
                 {
                    using var heartbeatScope = _serviceProvider.CreateScope();
                    var heartbeatDb = heartbeatScope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var mySession = await heartbeatDb.ActiveWorkers.FirstOrDefaultAsync(w => w.Id == _workerId, stoppingToken);
                    if (mySession != null)
                    {
                        mySession.LastHeartbeat = DateTimeOffset.UtcNow;
                        await heartbeatDb.SaveChangesAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update heartbeat for worker session {WorkerId}", _workerId);
                }
            }
        }, stoppingToken);

        // 3. Shared polling loop for queued executions and persisted work items
        using var pollTimer = new PeriodicTimer(PollInterval);
        while (await pollTimer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingWorkItemsAsync(stoppingToken);
                await DrainQueuedExecutionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while executing a workflow in the background worker loop.");
            }
        }

        // 4. Heartbeat cleanup on graceful stop
        try
        {
            using var cleanupScope = _serviceProvider.CreateScope();
            var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var mySession = await cleanupDb.ActiveWorkers.FirstOrDefaultAsync(w => w.Id == _workerId);
            if (mySession != null)
            {
                cleanupDb.ActiveWorkers.Remove(mySession);
                await cleanupDb.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clean up worker session {WorkerId} on stop.", _workerId);
        }

        _logger.LogInformation("Workflow Execution Worker stopped.");
    }

    private async Task DrainQueuedExecutionsAsync(CancellationToken stoppingToken)
    {
        while (_queue.TryDequeue(out var executionId))
        {
            _logger.LogInformation("Processing queued workflow execution instance: {ExecutionId}", executionId);

            if (_runSlots is null)
            {
                // Serial (default): await each run to completion before dequeuing the next.
                await RunOneAsync(executionId, stoppingToken);
            }
            else
            {
                // Bounded concurrency: acquire a slot, then run without awaiting so the next run can start.
                // The single poll-loop thread stays the sole channel reader; only execution fans out.
                await _runSlots.WaitAsync(stoppingToken);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RunOneAsync(executionId, stoppingToken);
                    }
                    finally
                    {
                        _runSlots.Release();
                    }
                }, stoppingToken);
            }
        }
    }

    private async Task RunOneAsync(ExecutionInstanceId executionId, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<WorkflowExecutor>();
            await executor.ExecuteAsync(executionId, null, null, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workflow execution {ExecutionId} failed in the background worker.", executionId);
        }
    }

    private async Task ProcessPendingWorkItemsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        var dueWorkItemIds = await dbContext.ExecutionWorkItems
            .Where(workItem => workItem.Status == WorkItemStatus.Pending &&
                               (!workItem.NotBeforeUtc.HasValue || workItem.NotBeforeUtc <= now))
            .OrderBy(workItem => workItem.CreatedAtUtc)
            .Select(workItem => workItem.Id)
            .Take(MaxWorkItemsPerCycle)
            .ToListAsync(stoppingToken);

        foreach (var workItemId in dueWorkItemIds)
        {
            var claimCount = await dbContext.ExecutionWorkItems
                .Where(workItem => workItem.Id == workItemId &&
                                   workItem.Status == WorkItemStatus.Pending &&
                                   (!workItem.NotBeforeUtc.HasValue || workItem.NotBeforeUtc <= now))
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(workItem => workItem.Status, WorkItemStatus.Running),
                    stoppingToken);

            if (claimCount != 1)
            {
                continue;
            }

            try
            {
                using var processingScope = _serviceProvider.CreateScope();
                var executor = processingScope.ServiceProvider.GetRequiredService<WorkflowExecutor>();
                await executor.ProcessWorkItemAsync(workItemId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process execution work item {WorkItemId}.", workItemId);

                // Return the item to Pending (with a short backoff) so a transient failure retries instead of
                // leaving it stuck in Running forever. A crash before this runs is covered by startup reclaim.
                try
                {
                    await dbContext.ExecutionWorkItems
                        .Where(workItem => workItem.Id == workItemId && workItem.Status == WorkItemStatus.Running)
                        .ExecuteUpdateAsync(updates => updates
                            .SetProperty(workItem => workItem.Status, WorkItemStatus.Pending)
                            .SetProperty(workItem => workItem.NotBeforeUtc, DateTimeOffset.UtcNow.AddSeconds(30)),
                            stoppingToken);
                }
                catch (Exception resetEx)
                {
                    _logger.LogWarning(resetEx, "Failed to reset stuck work item {WorkItemId} back to Pending.", workItemId);
                }
            }
        }
    }
}
