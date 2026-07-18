// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.NodeRuntime.Sandbox;

/// <summary>
/// Duplex message channel over one stream (a named pipe end). Owns a single read loop that
/// routes request/response pairs by correlation id and hands every other message to
/// <paramref name="onMessage"/>. Writes are serialized internally, so any number of
/// concurrent senders (e.g. an executor issuing state callbacks while the host sends a
/// cancel) can share the connection. Used by both the host runner and the worker process.
/// </summary>
public sealed class SandboxConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly Func<SandboxMessage, Task> _onMessage;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SandboxMessage>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readLoop;

    /// <summary>Completes when the peer closes the stream or the loop faults. Faulted = protocol error.</summary>
    public Task Completion => _readLoop;

    public SandboxConnection(Stream stream, Func<SandboxMessage, Task> onMessage)
    {
        _stream = stream;
        _onMessage = onMessage;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var message = await SandboxFraming.ReadAsync(_stream, _lifetime.Token).ConfigureAwait(false);
                if (message is null)
                {
                    break; // peer closed cleanly
                }

                // Responses complete their pending request; everything else goes to the handler.
                if (message.Id is not null && _pending.TryRemove(ResponseKey(message.Type, message.Id), out var tcs))
                {
                    tcs.TrySetResult(message);
                    continue;
                }

                await _onMessage(message).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // disposed
        }
        finally
        {
            // Fail all in-flight requests so no caller hangs on a dead connection.
            foreach (var kvp in _pending)
            {
                kvp.Value.TrySetException(new IOException("Sandbox connection closed."));
            }
            _pending.Clear();
        }
    }

    public async Task SendAsync(SandboxMessage message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SandboxFraming.WriteAsync(_stream, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Sends <paramref name="request"/> and awaits the message whose type is
    /// <paramref name="responseType"/> and whose id matches the request's id.
    /// </summary>
    public async Task<SandboxMessage> RequestAsync(
        SandboxMessage request, string responseType, CancellationToken cancellationToken)
    {
        var id = request.Id ?? throw new ArgumentException("Request message must carry an Id.", nameof(request));
        var tcs = new TaskCompletionSource<SandboxMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(ResponseKey(responseType, id), tcs))
        {
            throw new InvalidOperationException($"Duplicate in-flight request id '{id}'.");
        }

        try
        {
            await SendAsync(request, cancellationToken).ConfigureAwait(false);
            await using var registration = cancellationToken.Register(
                () => tcs.TrySetCanceled(cancellationToken)).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(ResponseKey(responseType, id), out _);
        }
    }

    private static string ResponseKey(string type, string id) => type + ":" + id;

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // best-effort: the pipe may already be broken (e.g. killed worker)
        }
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch
        {
            // loop faults surface via Completion for observers; disposal must not throw
        }
        _lifetime.Dispose();
        _writeLock.Dispose();
    }
}
