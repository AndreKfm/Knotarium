using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;

namespace KnotGarden.Api;

public class SseEventPublisher : IExecutionEventPublisher
{
    private readonly ConcurrentDictionary<ExecutionInstanceId, ConcurrentDictionary<Channel<ExecutionJournal>, byte>> _subscribers = new();

    public Task PublishAsync(ExecutionInstanceId executionId, ExecutionJournal entry, CancellationToken cancellationToken = default)
    {
        if (_subscribers.TryGetValue(executionId, out var channels))
        {
            foreach (var channel in channels.Keys)
            {
                channel.Writer.TryWrite(entry);
            }
        }
        return Task.CompletedTask;
    }

    public void Subscribe(ExecutionInstanceId executionId, Channel<ExecutionJournal> channel)
    {
        var channels = _subscribers.GetOrAdd(executionId, _ => new ConcurrentDictionary<Channel<ExecutionJournal>, byte>());
        channels.TryAdd(channel, 0);
    }

    public void Unsubscribe(ExecutionInstanceId executionId, Channel<ExecutionJournal> channel)
    {
        if (_subscribers.TryGetValue(executionId, out var channels))
        {
            channels.TryRemove(channel, out _);
            if (channels.IsEmpty)
            {
                _subscribers.TryRemove(executionId, out _);
            }
        }
    }
}
