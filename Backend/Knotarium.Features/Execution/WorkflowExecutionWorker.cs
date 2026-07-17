// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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
    private readonly ExecutionOptions _options;
    private readonly ExecutionRuntimeMonitor? _monitor;

    // Run-level concurrency gate. Every unit of run work — a fresh queued run AND a work-item resume —
    // must hold a slot while executing, so MaxConcurrentRuns bounds total overlap and 1 reproduces the
    // historical fully-serial behavior exactly (a resume can then never overlap a run either).
    // Each run executes in its own DI scope (own WorkflowExecutor + AppDbContext), so overlap is safe
    // against shared executor/DbContext state; SQLite stays single-writer, so concurrent runs' writes
    // serialize (WAL + busy_timeout) rather than corrupt.
    private readonly SemaphoreSlim _runSlots;

    // Every launched-but-unfinished run task, so shutdown can drain them and diagnostics can count them.
    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();

    public WorkflowExecutionWorker(
        WorkflowExecutionQueue queue,
        IServiceProvider serviceProvider,
        ILogger<WorkflowExecutionWorker> logger,
        ExecutionOptions? options = null,
        ExecutionRuntimeMonitor? monitor = null)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _workerId = Guid.NewGuid().ToString();
        _options = options ?? new ExecutionOptions();
        _monitor = monitor;
        _runSlots = new SemaphoreSlim(_options.MaxConcurrentRuns, _options.MaxConcurrentRuns);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Workflow Execution Worker started with ID: {WorkerId} (MaxConcurrentRuns: {MaxConcurrentRuns})",
            _workerId, _options.MaxConcurrentRuns);

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

        // 3. Shared dispatch loop for queued executions and persisted work items. The loop itself never
        // awaits a run: it acquires a run slot, launches the run as a tracked task, and moves on — so one
        // slow I/O-bound run no longer stalls every other queued run. It stays the single channel reader.
        using var pollTimer = new PeriodicTimer(PollInterval);
        while (await pollTimer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchPendingWorkItemsAsync(stoppingToken);
                DispatchQueuedExecutions(stoppingToken);
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

        // 4. Graceful shutdown: stop dispatching (done — the loop has exited), then drain in-flight runs
        // within a bounded timeout. Runs that don't finish rely on crash recovery on next start (their
        // Running/work-item state is persisted).
        await DrainInFlightRunsAsync();

        // 5. Heartbeat cleanup on graceful stop
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

    private void DispatchQueuedExecutions(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Acquire a run slot BEFORE dequeuing, so unstartable items stay buffered in the channel
            // (natural backpressure: the queue depth stays observable and the depth cap keeps meaning).
            if (!_runSlots.Wait(0))
            {
                return;
            }

            if (!_queue.TryDequeue(out var executionId))
            {
                _runSlots.Release();
                return;
            }

            _logger.LogInformation("Processing queued workflow execution instance: {ExecutionId}", executionId);
            LaunchTracked(() => RunOneAsync(executionId, stoppingToken));
        }
    }

    /// <summary>
    /// Launches one unit of run work (a fresh run or a work-item resume) as a tracked task. The caller
    /// must already hold a run slot; the slot is released when the task finishes, success or failure,
    /// so a crashing run can never leak a slot.
    /// </summary>
    private void LaunchTracked(Func<Task> work)
    {
        var key = Guid.NewGuid();
        _monitor?.RunStarted();

        var task = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            finally
            {
                _runSlots.Release();
                _monitor?.RunFinished();
            }
        });

        _inFlight[key] = task;
        _ = task.ContinueWith(
            _ => _inFlight.TryRemove(key, out var _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainInFlightRunsAsync()
    {
        var inFlight = _inFlight.Values.ToArray();
        if (inFlight.Length == 0)
        {
            return;
        }

        var timeout = TimeSpan.FromSeconds(_options.ShutdownDrainTimeoutSeconds);
        _logger.LogInformation(
            "Shutdown: waiting up to {TimeoutSeconds}s for {Count} in-flight run(s) to finish.",
            timeout.TotalSeconds, inFlight.Length);

        var drain = Task.WhenAll(inFlight);
        var finished = await Task.WhenAny(drain, Task.Delay(timeout)) == drain;
        if (!finished)
        {
            _logger.LogWarning(
                "Shutdown drain timeout elapsed with {Count} run(s) still in flight; crash recovery will reconcile them on next start.",
                _inFlight.Count);
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

    private async Task DispatchPendingWorkItemsAsync(CancellationToken stoppingToken)
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
            // Resumed runs share the run-slot budget with fresh runs, and the slot is acquired BEFORE
            // the atomic Pending→Running claim so an unstartable item stays Pending (never parked in
            // Running waiting for a slot).
            if (!_runSlots.Wait(0))
            {
                return;
            }

            var claimed = false;
            try
            {
                var claimCount = await dbContext.ExecutionWorkItems
                    .Where(workItem => workItem.Id == workItemId &&
                                       workItem.Status == WorkItemStatus.Pending &&
                                       (!workItem.NotBeforeUtc.HasValue || workItem.NotBeforeUtc <= now))
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(workItem => workItem.Status, WorkItemStatus.Running),
                        stoppingToken);

                claimed = claimCount == 1;
            }
            finally
            {
                if (!claimed)
                {
                    _runSlots.Release();
                }
            }

            if (!claimed)
            {
                continue;
            }

            LaunchTracked(() => ProcessOneWorkItemAsync(workItemId, stoppingToken));
        }
    }

    private async Task ProcessOneWorkItemAsync(Guid workItemId, CancellationToken stoppingToken)
    {
        try
        {
            using var processingScope = _serviceProvider.CreateScope();
            var executor = processingScope.ServiceProvider.GetRequiredService<WorkflowExecutor>();
            await executor.ProcessWorkItemAsync(workItemId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-processing: leave the item Running — startup reclaim returns it to Pending.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process execution work item {WorkItemId}.", workItemId);

            // Return the item to Pending (with a short backoff) so a transient failure retries instead of
            // leaving it stuck in Running forever. A crash before this runs is covered by startup reclaim.
            // Uses its own scope (the processing scope's context may be poisoned) and no cancellation token
            // (the reset should still land during shutdown).
            try
            {
                using var resetScope = _serviceProvider.CreateScope();
                var resetDb = resetScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await resetDb.ExecutionWorkItems
                    .Where(workItem => workItem.Id == workItemId && workItem.Status == WorkItemStatus.Running)
                    .ExecuteUpdateAsync(updates => updates
                        .SetProperty(workItem => workItem.Status, WorkItemStatus.Pending)
                        .SetProperty(workItem => workItem.NotBeforeUtc, DateTimeOffset.UtcNow.AddSeconds(30)),
                        CancellationToken.None);
            }
            catch (Exception resetEx)
            {
                _logger.LogWarning(resetEx, "Failed to reset stuck work item {WorkItemId} back to Pending.", workItemId);
            }
        }
    }
}
