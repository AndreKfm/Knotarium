using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Features.Execution;

/// <summary>
/// Writes an execution journal entry and publishes it to live listeners, in one call. Also the
/// single failure chokepoint: every <see cref="JournalEventTypes.WorkflowFailed"/> entry fans out
/// to the failure-alert and error-workflow queues.
/// </summary>
internal sealed class ExecutionJournalPublisher
{
    private readonly IExecutionJournalWriter _journalWriter;
    private readonly IExecutionEventPublisher _publisher;
    private readonly IFailureAlertSink? _failureAlertQueue;
    private readonly IErrorWorkflowSink? _errorWorkflowQueue;

    public ExecutionJournalPublisher(
        IExecutionJournalWriter journalWriter,
        IExecutionEventPublisher publisher,
        IFailureAlertSink? failureAlertQueue,
        IErrorWorkflowSink? errorWorkflowQueue)
    {
        _journalWriter = journalWriter;
        _publisher = publisher;
        _failureAlertQueue = failureAlertQueue;
        _errorWorkflowQueue = errorWorkflowQueue;
    }

    public async Task<ExecutionJournal> PublishAsync(
        ExecutionInstance instance,
        string eventType,
        string message,
        NodeId? nodeId = null,
        Dictionary<string, object>? data = null,
        CancellationToken cancellationToken = default)
    {
        var entry = new ExecutionJournal
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = instance.Id,
            NodeId = nodeId,
            Timestamp = DateTimeOffset.UtcNow,
            EventType = eventType,
            Message = message,
            Data = data ?? new Dictionary<string, object>()
        };

        // Write directly to IExecutionJournalWriter bypassing EF Core change-tracking overhead on hot-path
        await _journalWriter.WriteAsync(entry);

        await _publisher.PublishAsync(instance.Id, entry, cancellationToken);

        // Single failure chokepoint: every WorkflowFailed path flows through here. Enqueue is a
        // non-blocking in-memory hand-off, so it can never block or break the run.
        if (eventType == JournalEventTypes.WorkflowFailed)
        {
            _failureAlertQueue?.Enqueue(instance.Id);
            _errorWorkflowQueue?.Enqueue(instance.Id);
        }

        return entry;
    }
}
