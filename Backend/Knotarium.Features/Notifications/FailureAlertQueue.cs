// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Notifications;

/// <summary>A queued failure-alert dispatch plus how many delivery attempts it has already had.</summary>
public sealed record FailureAlertItem(ExecutionInstanceId ExecutionId, int Attempt);

/// <summary>
/// In-memory, non-blocking hand-off of failed execution ids from the workflow executor to the
/// <see cref="FailureAlertWorker"/>. Mirrors <c>WorkflowExecutionQueue</c> so that alert dispatch is
/// fully decoupled from execution and can never block or break a run. Items carry an attempt count so the
/// worker can apply a bounded retry with backoff on transient dispatch failures.
/// </summary>
public class FailureAlertQueue : IFailureAlertSink
{
    private readonly Channel<FailureAlertItem> _channel;

    public FailureAlertQueue()
    {
        _channel = Channel.CreateUnbounded<FailureAlertItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>Enqueues a failed execution for alert dispatch (first attempt). Non-blocking; safe on the hot path.</summary>
    public void Enqueue(ExecutionInstanceId executionId)
    {
        _channel.Writer.TryWrite(new FailureAlertItem(executionId, 0));
    }

    /// <summary>Re-enqueues an item for a further delivery attempt (used by the worker's bounded retry).</summary>
    public void Requeue(FailureAlertItem item)
    {
        _channel.Writer.TryWrite(item);
    }

    public ValueTask<FailureAlertItem> DequeueAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAsync(cancellationToken);
    }
}
