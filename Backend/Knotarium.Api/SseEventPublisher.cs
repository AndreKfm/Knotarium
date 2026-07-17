// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Api;

/// <summary>
/// In-process fan-out of journal entries to live SSE timeline subscribers. Bounded on two axes so a slow or
/// abandoned client can't exhaust memory: each subscriber's channel has a fixed capacity (oldest entries are
/// dropped under backpressure — the client reconnects and catches up from the DB), and the total number of
/// concurrent subscribers is capped. Configure via <c>Sse:ChannelCapacity</c> and <c>Sse:MaxSubscribers</c>.
/// </summary>
public class SseEventPublisher : IExecutionEventPublisher
{
    private readonly ConcurrentDictionary<ExecutionInstanceId, ConcurrentDictionary<Channel<ExecutionJournal>, byte>> _subscribers = new();
    private readonly int _maxSubscribers;
    private int _subscriberCount;

    public SseEventPublisher(IConfiguration configuration)
    {
        ChannelCapacity = Math.Max(16, configuration.GetValue("Sse:ChannelCapacity", 1000));
        _maxSubscribers = Math.Max(1, configuration.GetValue("Sse:MaxSubscribers", 200));
    }

    /// <summary>Capacity for a per-subscriber bounded channel (the endpoint sizes its channel from this).</summary>
    public int ChannelCapacity { get; }

    public Task PublishAsync(ExecutionInstanceId executionId, ExecutionJournal entry, CancellationToken cancellationToken = default)
    {
        if (_subscribers.TryGetValue(executionId, out var channels))
        {
            foreach (var channel in channels.Keys)
            {
                // Non-blocking; with a bounded DropOldest channel this always succeeds by evicting the oldest.
                channel.Writer.TryWrite(entry);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a bounded channel sized from <see cref="ChannelCapacity"/> (drops oldest under backpressure)
    /// and registers it, unless the global subscriber cap is reached. Returns null when the cap is hit so the
    /// endpoint can answer 503 rather than accept an unbounded number of live connections.
    /// </summary>
    public Channel<ExecutionJournal>? TrySubscribe(ExecutionInstanceId executionId)
    {
        // Reserve a slot first so the total can never exceed the cap under concurrency.
        if (Interlocked.Increment(ref _subscriberCount) > _maxSubscribers)
        {
            Interlocked.Decrement(ref _subscriberCount);
            return null;
        }

        var channel = Channel.CreateBounded<ExecutionJournal>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var channels = _subscribers.GetOrAdd(executionId, _ => new ConcurrentDictionary<Channel<ExecutionJournal>, byte>());
        channels.TryAdd(channel, 0);
        return channel;
    }

    public void Unsubscribe(ExecutionInstanceId executionId, Channel<ExecutionJournal> channel)
    {
        if (_subscribers.TryGetValue(executionId, out var channels))
        {
            if (channels.TryRemove(channel, out _))
            {
                Interlocked.Decrement(ref _subscriberCount);
            }
            if (channels.IsEmpty)
            {
                _subscribers.TryRemove(executionId, out _);
            }
        }
    }
}
