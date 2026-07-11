using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Compiler;
using KnotGarden.Infrastructure.Persistence;
using KnotGarden.Infrastructure.Security;

namespace KnotGarden.Features.Execution;

public partial class WorkflowExecutor
{
    private async Task<bool> TryScheduleRetryAsync(
        ExecutionInstance instance,
        NodeState nodeState,
        NodePackageManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.RecoveryMode != RecoveryMode.RetryAutomatically ||
            manifest.SideEffectKind != NodeSideEffectKind.IdempotentSideEffect)
        {
            return false;
        }

        var policy = manifest.RetryPolicy ?? new RetryPolicy();
        var attemptNumber = await GetAttemptNumberAsync(instance.Id, nodeState.NodeId, cancellationToken);
        if (attemptNumber >= policy.MaxAttempts)
        {
            return false;
        }

        var nextAttempt = attemptNumber + 1;
        var now = _timeProvider.GetUtcNow();
        var nextRetryAtUtc = now.Add(RetryBackoffCalculator.CalculateDelay(policy, nextAttempt));

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            instance.Status = ExecutionStatus.WaitingForRetry;
            instance.UpdatedAt = now;

            var workItem = new ExecutionWorkItem
            {
                Id = Guid.NewGuid(),
                ExecutionInstanceId = instance.Id,
                Type = "Retry",
                Payload = JsonSerializer.Serialize(new RetryWorkItemPayload(nodeState.NodeId.Value, nextAttempt, instance.WorkflowVersionId?.Value)),
                NotBeforeUtc = nextRetryAtUtc,
                Status = WorkItemStatus.Pending,
                CreatedAtUtc = now
            };

            await _dbContext.ExecutionWorkItems.AddAsync(workItem, cancellationToken);

            var retryState = await _dbContext.NodeRetryStates
                .SingleOrDefaultAsync(
                    state => state.ExecutionInstanceId == instance.Id && state.NodeId == nodeState.NodeId,
                    cancellationToken);

            if (retryState == null)
            {
                retryState = new NodeRetryState
                {
                    Id = Guid.NewGuid(),
                    ExecutionInstanceId = instance.Id,
                    NodeId = nodeState.NodeId,
                    AttemptNumber = nextAttempt,
                    NextRetryAtUtc = nextRetryAtUtc,
                    SanitizedFailureMessage = SanitizeFailureMessage(nodeState.ErrorMessage)
                };

                await _dbContext.NodeRetryStates.AddAsync(retryState, cancellationToken);
            }
            else
            {
                retryState.AttemptNumber = nextAttempt;
                retryState.NextRetryAtUtc = nextRetryAtUtc;
                retryState.SanitizedFailureMessage = SanitizeFailureMessage(nodeState.ErrorMessage);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<int> GetAttemptNumberAsync(
        ExecutionInstanceId executionInstanceId,
        NodeId nodeId,
        CancellationToken cancellationToken)
    {
        var retryState = await _dbContext.NodeRetryStates
            .SingleOrDefaultAsync(
                state => state.ExecutionInstanceId == executionInstanceId && state.NodeId == nodeId,
                cancellationToken);

        return retryState?.AttemptNumber ?? 1;
    }

    private async Task ClearRetryStateAsync(
        ExecutionInstanceId executionInstanceId,
        NodeId nodeId,
        CancellationToken cancellationToken)
    {
        var retryState = await _dbContext.NodeRetryStates
            .SingleOrDefaultAsync(
                state => state.ExecutionInstanceId == executionInstanceId && state.NodeId == nodeId,
                cancellationToken);

        if (retryState == null)
        {
            return;
        }

        _dbContext.NodeRetryStates.Remove(retryState);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static Dictionary<string, object> CreateFailureJournalData(
        string? errorMessage, Guid? attemptId, string? errorCode = null)
    {
        var data = new Dictionary<string, object>
        {
            ["error"] = errorMessage ?? "Node execution failed."
        };

        if (attemptId.HasValue)
        {
            data["AttemptId"] = attemptId.Value.ToString();
        }

        // R6: a discrete, field-queryable error code in the hash-chained audit Data (vs substring-matching
        // the message). Present only when the failing task supplied a structured code.
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            data["errorCode"] = errorCode;
        }

        return data;
    }

    private static Dictionary<string, object> CreateAttemptData(string? reason, string? attemptId)
    {
        var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(reason))
        {
            data["reason"] = reason;
        }

        if (!string.IsNullOrWhiteSpace(attemptId))
        {
            data["AttemptId"] = attemptId;
        }

        return data;
    }

    private static string? FindPendingAttemptId(IEnumerable<ExecutionJournal> journalEntries, NodeId nodeId)
    {
        var attemptEntries = journalEntries
            .Where(entry => entry.EventType == JournalEventTypes.AttemptingExternalEffect && entry.NodeId == nodeId)
            .OrderByDescending(entry => entry.Timestamp)
            .ToList();

        foreach (var attemptEntry in attemptEntries)
        {
            var attemptId = TryReadString(attemptEntry.Data, "AttemptId");
            if (string.IsNullOrWhiteSpace(attemptId))
            {
                continue;
            }

            var hasCompletion = journalEntries.Any(entry =>
                entry.NodeId == nodeId &&
                (entry.EventType == JournalEventTypes.NodeExecutionCompleted || entry.EventType == JournalEventTypes.NodeExecutionFailed) &&
                string.Equals(TryReadString(entry.Data, "AttemptId"), attemptId, StringComparison.OrdinalIgnoreCase));

            if (!hasCompletion)
            {
                return attemptId;
            }
        }

        return null;
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
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String => jsonElement.GetString(),
            _ => value.ToString()
        };
    }

    private static bool TryNormalizeManualDecision(string decision, out ManualDecision normalizedDecision)
    {
        normalizedDecision = default;

        if (string.IsNullOrWhiteSpace(decision))
        {
            return false;
        }

        return Enum.TryParse(decision, ignoreCase: true, out normalizedDecision);
    }

    private static string SanitizeFailureMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Node execution failed.";
        }

        var sanitized = Regex.Replace(
            message,
            "(?i)(authorization\\s*:\\s*bearer\\s+)[^\\s,;]+",
            "$1***");

        sanitized = Regex.Replace(
            sanitized,
            "(?i)\\b(token|password|secret|apikey)\\s*[:=]\\s*[^\\s,;]+",
            "$1=***");

        return sanitized;
    }

}
