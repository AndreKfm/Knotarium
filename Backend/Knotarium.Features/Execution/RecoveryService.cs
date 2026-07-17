// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Features.Execution;

/// <summary>
/// Recovers incomplete non-idempotent execution attempts that were interrupted mid-side-effect.
/// </summary>
public sealed class RecoveryService
{
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecoveryService"/> class.
    /// </summary>
    /// <param name="dbContext">The application database context.</param>
    public RecoveryService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Scans active executions for incomplete non-idempotent attempts and routes them to manual decision.
    /// </summary>
    /// <param name="cancellationToken">A token that can stop the recovery scan.</param>
    /// <returns>The number of execution instances updated by recovery.</returns>
    public async Task<int> RecoverIncompleteExternalEffectsAsync(CancellationToken cancellationToken = default)
    {
        var activeExecutions = await _dbContext.ExecutionInstances
            .Include(instance => instance.NodeStates)
            .Include(instance => instance.JournalEntries)
            .Where(instance => instance.Status == ExecutionStatus.Running || instance.Status == ExecutionStatus.WaitingForRetry)
            .ToListAsync(cancellationToken);

        var recoveredCount = 0;
        foreach (var instance in activeExecutions)
        {
            var pendingAttempts = instance.JournalEntries
                .Where(entry => entry.EventType == JournalEventTypes.AttemptingExternalEffect)
                .Select(entry => new
                {
                    AttemptId = TryReadString(entry.Data, "AttemptId"),
                    NodeId = TryReadString(entry.Data, "NodeId")
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.AttemptId) && !string.IsNullOrWhiteSpace(item.NodeId))
                .Where(item => !HasMatchingCompletion(instance.JournalEntries, item.AttemptId!))
                .ToList();

            if (pendingAttempts.Count == 0)
            {
                continue;
            }

            var changed = false;
            foreach (var pendingAttempt in pendingAttempts)
            {
                var nodeId = NodeId.Create(pendingAttempt.NodeId!);
                var nodeState = instance.NodeStates.FirstOrDefault(state => state.NodeId == nodeId);
                if (nodeState == null)
                {
                    continue;
                }

                nodeState.Status = NodeStatus.RequiresManualDecision;
                nodeState.ErrorMessage = "Execution interrupted during a non-idempotent side effect. Manual decision required.";
                changed = true;

                instance.JournalEntries.Add(new ExecutionJournal
                {
                    Id = Guid.NewGuid(),
                    ExecutionInstanceId = instance.Id,
                    NodeId = nodeId,
                    Timestamp = DateTimeOffset.UtcNow,
                    EventType = JournalEventTypes.ManualDecisionRecorded,
                    Message = $"Manual decision required for node '{nodeId.Value}' after interrupted external effect.",
                    Data = new Dictionary<string, object>
                    {
                        ["AttemptId"] = pendingAttempt.AttemptId!,
                        ["Reason"] = "InterruptedExternalEffect"
                    }
                });
            }

            if (!changed)
            {
                continue;
            }

            instance.Status = ExecutionStatus.Suspended;
            instance.UpdatedAt = DateTimeOffset.UtcNow;
            recoveredCount++;
        }

        if (recoveredCount > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return recoveredCount;
    }

    // Statuses that are already terminal — never touched by crash recovery.
    private static readonly ExecutionStatus[] TerminalStatuses =
    {
        ExecutionStatus.Completed,
        ExecutionStatus.Failed,
        ExecutionStatus.Cancelled,
        ExecutionStatus.Discarded,
    };

    /// <summary>
    /// Fail runs left stranded in <see cref="ExecutionStatus.Running"/> by a crash. The single-writer model
    /// guarantees that at worker startup — once the startup guard has confirmed no other worker owns the
    /// database and this worker has not begun processing — nothing is actually executing, so any run still
    /// marked Running is orphaned: it would otherwise sit in Running forever (never resumed, and never pruned
    /// because Running is not terminal). Runs interrupted mid-side-effect are handled first by
    /// <see cref="RecoverIncompleteExternalEffectsAsync"/> (routed to manual decision / Suspended); this
    /// marks the remainder Failed so they become terminal and, if desired, replayable.
    /// </summary>
    public async Task<int> FailOrphanedRunningRunsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var orphans = await _dbContext.ExecutionInstances
            .Where(instance => instance.Status == ExecutionStatus.Running)
            .ToListAsync(cancellationToken);

        foreach (var run in orphans)
        {
            run.Status = ExecutionStatus.Failed;
            run.UpdatedAt = now;
            _dbContext.JournalEntries.Add(new ExecutionJournal
            {
                Id = Guid.NewGuid(),
                ExecutionInstanceId = run.Id,
                NodeId = null,
                Timestamp = now,
                EventType = JournalEventTypes.NodeExecutionFailed,
                Message = "Run was interrupted by a process restart while executing and has been marked Failed by crash recovery. Replay it to re-run.",
            });
        }

        if (orphans.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return orphans.Count;
    }

    /// <summary>
    /// Reclaim execution work items stuck in <see cref="WorkItemStatus.Running"/> after a crash by returning
    /// them to <see cref="WorkItemStatus.Pending"/> so the worker reprocesses them — but only for executions
    /// that are still resumable (not terminal), so a work item belonging to a run just failed by
    /// <see cref="FailOrphanedRunningRunsAsync"/> is not revived. A work item is claimed Pending→Running for
    /// processing; if the process dies between the claim and completion, the plain <c>Pending</c> scan would
    /// never see it again.
    /// </summary>
    public async Task<int> ReclaimStuckWorkItemsAsync(CancellationToken cancellationToken = default)
    {
        var idsToReclaim = await _dbContext.ExecutionWorkItems
            .Where(workItem => workItem.Status == WorkItemStatus.Running
                && _dbContext.ExecutionInstances.Any(e => e.Id == workItem.ExecutionInstanceId && !TerminalStatuses.Contains(e.Status)))
            .Select(workItem => workItem.Id)
            .ToListAsync(cancellationToken);

        if (idsToReclaim.Count == 0)
        {
            return 0;
        }

        return await _dbContext.ExecutionWorkItems
            .Where(workItem => idsToReclaim.Contains(workItem.Id) && workItem.Status == WorkItemStatus.Running)
            .ExecuteUpdateAsync(updates => updates.SetProperty(workItem => workItem.Status, WorkItemStatus.Pending), cancellationToken);
    }

    /// <summary>
    /// The ids of runs still queued (<see cref="ExecutionStatus.Pending"/>) but never started. The in-memory
    /// run queue is lost on restart, so these would otherwise never execute. A Pending run has not entered the
    /// executor (which flips it to Running immediately), so re-queuing it cannot double up side effects.
    /// </summary>
    public async Task<IReadOnlyList<ExecutionInstanceId>> GetPendingRunIdsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.ExecutionInstances
            .Where(instance => instance.Status == ExecutionStatus.Pending)
            .Select(instance => instance.Id)
            .ToListAsync(cancellationToken);
    }

    private static bool HasMatchingCompletion(IEnumerable<ExecutionJournal> entries, string attemptId)
    {
        return entries.Any(entry =>
            (entry.EventType == JournalEventTypes.NodeExecutionCompleted || entry.EventType == JournalEventTypes.NodeExecutionFailed) &&
            string.Equals(TryReadString(entry.Data, "AttemptId"), attemptId, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryReadString(IReadOnlyDictionary<string, object>? data, string key)
    {
        if (data == null || !data.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            string stringValue => stringValue,
            Guid guidValue => guidValue.ToString(),
            _ => value.ToString()
        };
    }
}