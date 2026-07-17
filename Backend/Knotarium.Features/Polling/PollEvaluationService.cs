// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Knotarium.Features.Polling;

/// <summary>Evaluates due polling triggers and conditionally enqueues runs.</summary>
public sealed partial class PollEvaluationService : IPollEvaluationService
{
    private readonly IPollingTriggerStore _triggerStore;
    private readonly PollSourceRegistry _sourceRegistry;
    private readonly IPollRunEnqueuer _runEnqueuer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PollEvaluationService> _logger;

    public PollEvaluationService(
        IPollingTriggerStore triggerStore,
        PollSourceRegistry sourceRegistry,
        IPollRunEnqueuer runEnqueuer,
        TimeProvider timeProvider,
        ILogger<PollEvaluationService> logger)
    {
        _triggerStore = triggerStore ?? throw new ArgumentNullException(nameof(triggerStore));
        _sourceRegistry = sourceRegistry ?? throw new ArgumentNullException(nameof(sourceRegistry));
        _runEnqueuer = runEnqueuer ?? throw new ArgumentNullException(nameof(runEnqueuer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EvaluateDuePollsAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var dueTriggers = await _triggerStore.GetDueAsync(now, cancellationToken);

        foreach (var trigger in dueTriggers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessTriggerAsync(trigger, now, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Log.PollEvaluationFailed(_logger, trigger.Id, exception);

                // Recording the failure must never abort evaluation of the remaining triggers.
                // If the context was left in a bad state by the original failure, a second
                // SaveChanges can also throw — swallow it so one bad trigger can't stall the rest.
                try
                {
                    await RecordFailureAsync(trigger, now, exception.Message, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception recordException)
                {
                    Log.PollFailureRecordingFailed(_logger, trigger.Id, recordException);
                }
            }
        }
    }

    private async Task ProcessTriggerAsync(PollingTrigger trigger, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sourceKind = ReadSourceKind(trigger.ConfigJson);
        var source = _sourceRegistry.Resolve(sourceKind);

        var result = await source.PollAsync(new PollContext(trigger.ConfigJson, trigger.Cursor), cancellationToken);

        if (result.HasNew)
        {
            var created = await _runEnqueuer.EnqueueAsync(trigger.WorkflowDefinitionId, result.Payload, cancellationToken);
            if (created)
            {
                trigger.Cursor = result.NewCursor;
            }
            else
            {
                Log.MissingActiveVersionSkipped(_logger, trigger.Id, trigger.WorkflowDefinitionId.Value);
            }
        }

        trigger.NextPollAtUtc = now.AddSeconds(trigger.IntervalSeconds);
        trigger.LastPolledAtUtc = now;
        trigger.LastError = null;
        await _triggerStore.SaveAsync(trigger, cancellationToken);
    }

    private async Task RecordFailureAsync(PollingTrigger trigger, DateTimeOffset now, string error, CancellationToken cancellationToken)
    {
        trigger.NextPollAtUtc = now.AddSeconds(trigger.IntervalSeconds); // advance even on failure: no hammering
        trigger.LastPolledAtUtc = now;
        trigger.LastError = error;
        await _triggerStore.SaveAsync(trigger, cancellationToken);
    }

    private static string ReadSourceKind(string configJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(configJson);
        return doc.RootElement.TryGetProperty("sourceKind", out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String
            ? prop.GetString()!
            : "http";
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1300, Level = LogLevel.Error, Message = "Failed to evaluate polling trigger {TriggerId}.")]
        public static partial void PollEvaluationFailed(ILogger logger, Guid triggerId, Exception exception);

        [LoggerMessage(EventId = 1301, Level = LogLevel.Warning, Message = "Polling trigger {TriggerId} skipped enqueue because workflow {WorkflowId} has no active version.")]
        public static partial void MissingActiveVersionSkipped(ILogger logger, Guid triggerId, string workflowId);

        [LoggerMessage(EventId = 1302, Level = LogLevel.Error, Message = "Failed to record poll failure for trigger {TriggerId}; continuing with remaining triggers.")]
        public static partial void PollFailureRecordingFailed(ILogger logger, Guid triggerId, Exception exception);
    }
}
