// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Knotarium.NodeRuntime.Sandbox;

namespace Knotarium.Features.Nodes.Sandbox;

/// <summary>
/// One spawned worker process plus its pipe connection and confinement handle. A handle is
/// used by exactly one execution at a time (the pool guarantees exclusivity). Killing the
/// process (hard timeout) poisons the handle; the pool then spawns a replacement.
/// </summary>
internal sealed class SandboxWorkerHandle : IAsyncDisposable
{
    public required Process Process { get; init; }
    // Set right after construction: the connection's message router needs a reference to this handle.
    public SandboxConnection Connection { get; set; } = null!;
    public required ISandboxConfinement Confinement { get; init; }
    public int RunsCompleted;
    public volatile bool Poisoned;

    /// <summary>Routes non-response messages (callbacks, logs) for the execution currently borrowing this worker.</summary>
    public volatile Func<SandboxMessage, Task>? MessageHandler;

    public void Kill()
    {
        Poisoned = true;
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // already gone
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        await Connection.DisposeAsync().ConfigureAwait(false);
        Confinement.Dispose();
        Process.Dispose();
    }
}

/// <summary>
/// Bounded pool of sandbox workers. Borrow → run → return; a worker is recycled after
/// <see cref="SandboxOptions.RecycleAfterRuns"/> executions or when poisoned (killed /
/// exited). Spawning is lazy: workers start on first demand, not at host startup.
/// </summary>
internal sealed class SandboxWorkerPool : IAsyncDisposable
{
    private readonly SandboxOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<SandboxWorkerHandle?> _idle;
    private readonly SemaphoreSlim _spawnLock = new(1, 1);
    private int _spawned;
    private bool _disposed;

    public SandboxWorkerPool(SandboxOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _idle = Channel.CreateUnbounded<SandboxWorkerHandle?>();
        // Seed with vouchers: each null entry is the right to spawn one worker on demand.
        for (var i = 0; i < options.WorkerCount; i++)
        {
            _idle.Writer.TryWrite(null);
        }
    }

    public async Task<SandboxWorkerHandle> AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var handle = await _idle.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (handle is null)
            {
                return await SpawnAsync(cancellationToken).ConfigureAwait(false);
            }
            if (handle.Poisoned || handle.Process.HasExited)
            {
                await RetireAsync(handle).ConfigureAwait(false);
                // its slot converts back into a spawn voucher
                _idle.Writer.TryWrite(null);
                continue;
            }
            return handle;
        }
    }

    public async Task ReleaseAsync(SandboxWorkerHandle handle)
    {
        handle.MessageHandler = null;
        var runs = Interlocked.Increment(ref handle.RunsCompleted);
        if (_disposed || handle.Poisoned || handle.Process.HasExited || runs >= _options.RecycleAfterRuns)
        {
            await RetireAsync(handle).ConfigureAwait(false);
            _idle.Writer.TryWrite(null);
            return;
        }
        _idle.Writer.TryWrite(handle);
    }

    private async Task RetireAsync(SandboxWorkerHandle handle)
    {
        Interlocked.Decrement(ref _spawned);
        await handle.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<SandboxWorkerHandle> SpawnAsync(CancellationToken cancellationToken)
    {
        // Serialize spawns: cheap, rare, and avoids a thundering herd of process starts.
        await _spawnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pipeName = $"knotarium-sbx-{Guid.NewGuid():N}";
            var server = CreatePipeServer(pipeName);

            var (process, resume) = StartWorkerProcess(pipeName);
            var confinement = SandboxConfinementFactory.Create(_options, _logger);
            // Restricted launches start suspended, so the Job Object is in force before the
            // worker executes its first instruction; resume() is a no-op for plain launches.
            confinement.Apply(process);
            resume();

            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                await server.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                confinement.Dispose();
                await server.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("Sandbox worker failed to connect within 15s.");
            }

            var handle = new SandboxWorkerHandle
            {
                Process = process,
                Confinement = confinement
            };
            // The connection routes every non-response message to whichever execution currently
            // borrows this worker; between executions the handler is null and messages are dropped.
            handle.Connection = new SandboxConnection(server, msg =>
                handle.MessageHandler is { } handler ? handler(msg) : Task.CompletedTask);

            Interlocked.Increment(ref _spawned);
            _logger.LogInformation("Sandbox: spawned worker {Pid} ({Count} active).", process.Id, _spawned);
            return handle;
        }
        finally
        {
            _spawnLock.Release();
        }
    }

    /// <summary>Pipe server whose ACL admits a Low-integrity client: the restricted worker runs at
    /// Low IL, and Windows' no-write-up rule would otherwise block it from opening the pipe.</summary>
    private NamedPipeServerStream CreatePipeServer(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new PipeSecurity();
            // Local, single-instance, GUID-named pipe that the worker claims immediately; the
            // broad DACL exists solely so the restricted token's access check can pass.
            security.SetSecurityDescriptorSddlForm("D:(A;;GA;;;WD)");
            var server = NamedPipeServerStreamAcl.Create(
                pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 0, outBufferSize: 0, security);
            try
            {
                // Applied post-create: creating with a SACL inline needs SeSecurityPrivilege,
                // stamping a label afterwards does not.
                WindowsRestrictedProcessLauncher.AllowLowIntegrityAccess(server.SafePipeHandle);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sandbox: could not lower the pipe's integrity label; " +
                    "a restricted (Low-IL) worker may fail to connect.");
            }
            return server;
        }

        return new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    private (Process Process, Action Resume) StartWorkerProcess(string pipeName)
    {
        // The worker ships next to the host binaries. Prefer the apphost exe (Windows) and fall
        // back to "dotnet <dll>" so tests and non-Windows hosts work identically.
        var baseDir = AppContext.BaseDirectory;
        var exe = Path.Combine(baseDir, OperatingSystem.IsWindows() ? "Knotarium.SandboxWorker.exe" : "Knotarium.SandboxWorker");
        var dll = Path.Combine(baseDir, "Knotarium.SandboxWorker.dll");

        if (OperatingSystem.IsWindows() && _options.RestrictedToken && File.Exists(exe))
        {
            try
            {
                // Suspended + privilege-stripped + Low IL; the caller resumes after the Job
                // Object is attached.
                return WindowsRestrictedProcessLauncher.Start(exe, pipeName, baseDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Sandbox: restricted-token launch failed; starting the worker without token " +
                    "restriction (set Security:Sandbox:RestrictedToken=false to silence this).");
            }
        }

        ProcessStartInfo psi;
        if (File.Exists(exe))
        {
            psi = new ProcessStartInfo(exe, pipeName);
        }
        else if (File.Exists(dll))
        {
            psi = new ProcessStartInfo("dotnet", $"\"{dll}\" {pipeName}");
        }
        else
        {
            throw new InvalidOperationException(
                $"Sandbox worker binary not found next to the host ({baseDir}). " +
                "Ensure Knotarium.SandboxWorker is published alongside Knotarium.Api.");
        }

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = baseDir;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start sandbox worker process.");
        return (process, () => { });
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _idle.Writer.TryComplete();
        while (_idle.Reader.TryRead(out var handle))
        {
            if (handle is not null)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
        }
        _spawnLock.Dispose();
    }
}
