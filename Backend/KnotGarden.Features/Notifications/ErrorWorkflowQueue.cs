using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;

namespace KnotGarden.Features.Notifications;

/// <summary>
/// In-memory, non-blocking hand-off of failed execution ids from the workflow executor to the
/// <see cref="ErrorWorkflowWorker"/>. A sibling of <see cref="FailureAlertQueue"/> so that starting
/// the global error-handler workflow is fully decoupled from execution and can never block a run.
/// </summary>
public class ErrorWorkflowQueue : IErrorWorkflowSink
{
    private readonly Channel<ExecutionInstanceId> _channel;

    public ErrorWorkflowQueue()
    {
        _channel = Channel.CreateUnbounded<ExecutionInstanceId>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>Enqueues a failed execution for error-workflow dispatch. Non-blocking; safe on the hot path.</summary>
    public void Enqueue(ExecutionInstanceId executionId)
    {
        _channel.Writer.TryWrite(executionId);
    }

    public ValueTask<ExecutionInstanceId> DequeueAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAsync(cancellationToken);
    }
}
