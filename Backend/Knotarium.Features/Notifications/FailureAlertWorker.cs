using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Notifications;

/// <summary>
/// Background service that drains <see cref="FailureAlertQueue"/> and sends failure alerts over the
/// channels configured for the workflow (or the global default channels). Every dispatch is wrapped
/// so a delivery failure is journaled but never propagates back into workflow execution.
/// </summary>
public class FailureAlertWorker : BackgroundService
{
    // Bounded retry: transient dispatch failures (e.g. a momentary DB read error) are re-queued with a
    // short backoff up to this many total attempts, then dropped so a poison item can't loop forever.
    private const int MaxDeliveryAttempts = 3;

    private readonly FailureAlertQueue _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FailureAlertWorker> _logger;

    public FailureAlertWorker(
        FailureAlertQueue queue,
        IServiceProvider serviceProvider,
        ILogger<FailureAlertWorker> logger)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Failure Alert Worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            FailureAlertItem item;
            try
            {
                item = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await DispatchAsync(item.ExecutionId, stoppingToken);
            }
            catch (Exception ex)
            {
                // Last-resort guard: alerting must never crash the worker loop. Retry transient failures a
                // bounded number of times with backoff before giving up, so an alert isn't silently dropped
                // on the first hiccup.
                _logger.LogError(ex, "Failed to dispatch failure alert for execution {ExecutionId} (attempt {Attempt}).", item.ExecutionId, item.Attempt + 1);
                ScheduleRetry(item, stoppingToken);
            }
        }

        _logger.LogInformation("Failure Alert Worker stopped.");
    }

    private void ScheduleRetry(FailureAlertItem item, CancellationToken stoppingToken)
    {
        var nextAttempt = item.Attempt + 1;
        if (nextAttempt >= MaxDeliveryAttempts)
        {
            _logger.LogError(
                "Giving up on failure alert for execution {ExecutionId} after {Attempts} attempt(s).",
                item.ExecutionId, MaxDeliveryAttempts);
            return;
        }

        var backoff = TimeSpan.FromSeconds(Math.Min(30, 5 * nextAttempt));
        // Fire-and-forget delayed re-queue so the drain loop isn't blocked; wrapped so nothing escapes.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(backoff, stoppingToken);
                _queue.Requeue(item with { Attempt = nextAttempt });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down — drop the retry.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to schedule failure-alert retry for execution {ExecutionId}.", item.ExecutionId);
            }
        }, stoppingToken);
    }

    private async Task DispatchAsync(ExecutionInstanceId executionId, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var readStore = scope.ServiceProvider.GetRequiredService<IExecutionReadStore>();
        var channelStore = scope.ServiceProvider.GetRequiredService<INotificationChannelStore>();
        var workflowStore = scope.ServiceProvider.GetRequiredService<IWorkflowStore>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();

        var instance = await readStore.GetInstanceWithNodeStatesAsync(executionId, cancellationToken);

        if (instance is null)
        {
            return;
        }

        var workflow = await workflowStore.GetAsync(instance.WorkflowDefinitionId, cancellationToken);

        var allChannels = await channelStore.ListAsync(cancellationToken);
        var channels = FailureAlertChannelResolver.Resolve(workflow?.Metadata?.FailureAlert, allChannels);
        if (channels.Count == 0)
        {
            return;
        }

        var message = (await FailureContextBuilder.BuildAsync(readStore, instance, workflow, cancellationToken)).ToNotification();

        var journalWriter = scope.ServiceProvider.GetRequiredService<IExecutionJournalWriter>();
        var publisher = scope.ServiceProvider.GetRequiredService<IExecutionEventPublisher>();

        foreach (var channel in channels)
        {
            try
            {
                await dispatcher.SendAsync(channel, message, cancellationToken);
                await WriteJournalAsync(journalWriter, publisher, executionId,
                    JournalEventTypes.NotificationSent,
                    $"Failure alert sent via channel '{channel.Name}'.",
                    channel, cancellationToken);
            }
            catch (Exception ex)
            {
                await WriteJournalAsync(journalWriter, publisher, executionId,
                    JournalEventTypes.NotificationFailed,
                    $"Failed to send failure alert via channel '{channel.Name}': {ex.Message}",
                    channel, cancellationToken);
                _logger.LogWarning(ex, "Failure alert delivery failed for channel {ChannelId} on execution {ExecutionId}.", channel.Id, executionId);
            }
        }
    }

    private static async Task WriteJournalAsync(
        IExecutionJournalWriter journalWriter,
        IExecutionEventPublisher publisher,
        ExecutionInstanceId executionId,
        string eventType,
        string messageText,
        NotificationChannel channel,
        CancellationToken cancellationToken)
    {
        var entry = new ExecutionJournal
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = executionId,
            NodeId = null,
            Timestamp = DateTimeOffset.UtcNow,
            EventType = eventType,
            Message = messageText,
            Data = new Dictionary<string, object>
            {
                ["channelId"] = channel.Id,
                ["channelName"] = channel.Name,
                ["channelType"] = channel.Type.ToString()
            }
        };

        await journalWriter.WriteAsync(entry);
        await publisher.PublishAsync(executionId, entry, cancellationToken);
    }
}
