// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Features.Execution;

/// <summary>
/// Schedules automatic retries for idempotent nodes whose manifest opts in
/// (<see cref="RecoveryMode.RetryAutomatically"/>): parks the run in
/// <see cref="ExecutionStatus.WaitingForRetry"/>, enqueues a delayed <c>Retry</c> work item per the
/// backoff policy, and tracks per-node attempt state.
/// </summary>
internal sealed class NodeRetryScheduler
{
    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public NodeRetryScheduler(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<bool> TryScheduleRetryAsync(
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

    public async Task ClearRetryStateAsync(
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
