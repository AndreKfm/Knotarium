// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Knotarium.Features.Ai;

/// <summary>
/// In-memory, non-blocking hand-off of generation job ids from the API endpoint to the
/// <see cref="AiGenerationWorker"/>. Mirrors <c>FailureAlertQueue</c>: the endpoint creates a job and
/// enqueues its id, the worker drains and runs it, so a multi-second generation never blocks the request.
/// </summary>
public sealed class AiGenerationQueue
{
    private readonly Channel<string> _channel;

    public AiGenerationQueue()
    {
        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public void Enqueue(string jobId) => _channel.Writer.TryWrite(jobId);

    public ValueTask<string> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}
