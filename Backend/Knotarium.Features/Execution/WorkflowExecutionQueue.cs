using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Execution;

public class WorkflowExecutionQueue : IWorkflowExecutionQueue
{
    private readonly Channel<ExecutionInstanceId> _channel;
    private readonly int _maxDepth;
    private int _depth;

    public WorkflowExecutionQueue(ExecutionOptions? options = null)
    {
        _maxDepth = (options ?? new ExecutionOptions()).MaxQueueDepth;
        _channel = Channel.CreateUnbounded<ExecutionInstanceId>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>Runs currently queued but not yet dequeued by the worker.</summary>
    public int Depth => Volatile.Read(ref _depth);

    /// <summary>The soft cap enforced by <see cref="TryQueueExecution"/>.</summary>
    public int MaxDepth => _maxDepth;

    /// <summary>Whether the depth cap is reached — the pre-flight check for rejectable start paths.</summary>
    public bool IsFull => Depth >= _maxDepth;

    public void QueueExecution(ExecutionInstanceId executionId)
    {
        // Internal producers (recovery re-queue, schedule/poll/error enqueuers) are never rejected:
        // their runs are already persisted as Pending, and a dropped enqueue would strand them until
        // the next restart. The cap is enforced only on the rejectable paths via TryQueueExecution.
        Interlocked.Increment(ref _depth);
        _channel.Writer.TryWrite(executionId);
    }

    /// <summary>
    /// Capacity-checked enqueue for externally-triggered start paths (manual run, webhook). Returns
    /// <see langword="false"/> when the queue is at its depth cap so the caller can reject with 429
    /// instead of growing memory unbounded. The check-then-write is racy by design — the cap is a soft
    /// backpressure bound, not an exact limit.
    /// </summary>
    public bool TryQueueExecution(ExecutionInstanceId executionId)
    {
        if (IsFull)
        {
            return false;
        }

        QueueExecution(executionId);
        return true;
    }

    public ValueTask<ExecutionInstanceId> DequeueAsync(CancellationToken cancellationToken)
    {
        return DequeueTrackedAsync(cancellationToken);
    }

    private async ValueTask<ExecutionInstanceId> DequeueTrackedAsync(CancellationToken cancellationToken)
    {
        var executionId = await _channel.Reader.ReadAsync(cancellationToken);
        Interlocked.Decrement(ref _depth);
        return executionId;
    }

    public bool TryDequeue(out ExecutionInstanceId executionId)
    {
        if (_channel.Reader.TryRead(out executionId))
        {
            Interlocked.Decrement(ref _depth);
            return true;
        }

        return false;
    }
}
