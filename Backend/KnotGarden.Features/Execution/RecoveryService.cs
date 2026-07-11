using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using KnotGarden.Core.Domain;
using KnotGarden.Infrastructure.Persistence;

namespace KnotGarden.Features.Execution;

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