// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text.Json;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.NodeRuntime;
using Knotarium.NodeRuntime.Sandbox;

namespace Knotarium.SandboxWorker;

/// <summary>
/// The worker side of the sandbox pipe: serves execute requests until the host closes the
/// connection. Each execution loads the shipped assembly into a fresh collectible load
/// context, runs the executor against a proxy <see cref="INodeContext"/> that forwards
/// every host interaction (log, state, HTTP, secrets) back over the pipe, replies with the
/// result, and unloads the context.
/// </summary>
internal sealed class WorkerSession
{
    private readonly SandboxConnection _connection;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public WorkerSession(Stream stream)
    {
        _connection = new SandboxConnection(stream, HandleMessageAsync);
    }

    public async Task RunAsync()
    {
        // The read loop drives everything; we're done when the host closes the pipe.
        try
        {
            await _connection.Completion.ConfigureAwait(false);
        }
        catch (IOException)
        {
            // host went away — normal shutdown for a disposable worker
        }
        catch (Exception ex)
        {
            // Protocol fault: surface it on stderr for diagnosis (invisible in production, where
            // stderr is not redirected) and exit; the host will treat the silence as a dead worker.
            Console.Error.WriteLine($"[sandbox-worker] read loop faulted: {ex}");
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private Task HandleMessageAsync(SandboxMessage message)
    {
        switch (message.Type)
        {
            case SandboxMessageTypes.Execute:
                // Run on the pool so the read loop stays free to serve callbacks and cancels. A
                // fault here (e.g. a missing dependency at JIT time) must still produce a reply —
                // a silent drop would leave the host waiting for the hard deadline.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteAsync(message).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[sandbox-worker] execute crashed: {ex}");
                        await ReplyAsync(message.Id ?? "?", null, null,
                            nameof(Knotarium.Core.Domain.NodeExecutionStatus.Failed),
                            $"Sandbox worker failed to execute: {ex.Message}").ConfigureAwait(false);
                    }
                });
                break;

            case SandboxMessageTypes.Cancel:
                if (message.Id is not null && _running.TryGetValue(message.Id, out var cts))
                {
                    cts.Cancel();
                }
                break;
        }
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(SandboxMessage request)
    {
        var id = request.Id!;
        using var cts = new CancellationTokenSource();
        _running[id] = cts;
        if (request.TimeoutSeconds > 0)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        }

        CollectibleAssemblyLoadContext? loadContext = null;
        try
        {
            loadContext = new CollectibleAssemblyLoadContext($"SandboxExec_{id}");
            var assembly = loadContext.LoadFromBytes(request.AssemblyBytes
                ?? throw new InvalidOperationException("Execute request carried no assembly."));

            var executorType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(INodeExecutor).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                ?? throw new InvalidOperationException("No concrete INodeExecutor found in the shipped assembly.");

            // Host services cannot cross the process boundary, so only parameterless executors
            // run out-of-process; the host filters accordingly and falls back in-process otherwise.
            var executor = (INodeExecutor)Activator.CreateInstance(executorType)!;

            var inputs = request.Inputs ?? new Dictionary<string, JsonElement>();
            var context = new ProxyNodeContext(_connection, cts.Token);

            var result = await executor.ExecuteAsync(new NodeInput(inputs), context, cts.Token).ConfigureAwait(false);

            await ReplyAsync(id, result.OutputName, result.Payload, result.Status.ToString(), error: null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            await ReplyAsync(id, null, null, nameof(NodeExecutionStatus.Cancelled), "Execution cancelled.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ReplyAsync(id, null, null, nameof(NodeExecutionStatus.Failed), ex.Message).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(id, out _);
            loadContext?.Unload();
        }
    }

    private async Task ReplyAsync(string id, string? outputName, JsonElement? payload, string status, string? error)
    {
        try
        {
            await _connection.SendAsync(new SandboxMessage
            {
                Type = SandboxMessageTypes.ExecuteResult,
                Id = id,
                OutputName = outputName,
                Payload = payload,
                Status = status,
                Error = error
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // host already gone; nothing to report to
            Console.Error.WriteLine($"[sandbox-worker] reply failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[sandbox-worker] reply failed: {ex}");
        }
    }
}
