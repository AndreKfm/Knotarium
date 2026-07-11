using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;

namespace KnotGarden.Features.Execution;

public class WorkflowExecutionQueue : IWorkflowExecutionQueue
{
    private readonly Channel<ExecutionInstanceId> _channel;

    public WorkflowExecutionQueue()
    {
        _channel = Channel.CreateUnbounded<ExecutionInstanceId>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public void QueueExecution(ExecutionInstanceId executionId)
    {
        _channel.Writer.TryWrite(executionId);
    }

    public ValueTask<ExecutionInstanceId> DequeueAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAsync(cancellationToken);
    }

    public bool TryDequeue(out ExecutionInstanceId executionId)
    {
        return _channel.Reader.TryRead(out executionId);
    }
}
