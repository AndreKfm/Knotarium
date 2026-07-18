// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Nodes.Sandbox;

/// <summary>
/// Routes every execution by the <b>current</b> <see cref="SandboxOptions.Mode"/>, so an admin
/// flipping the mode in Settings takes effect immediately — no restart, no DI rebuild. The
/// process-backed runner (worker pool) is created lazily on the first Process-mode execution
/// and kept alive thereafter; switching back to InProcess simply stops routing to it (idle
/// workers are reclaimed by recycling, and disposal happens on host shutdown).
/// </summary>
public sealed class SwitchableSandboxRunner : ISandboxRunner, IAsyncDisposable, IDisposable
{
    private readonly SandboxOptions _options;
    private readonly CSharpScriptCompiler _compiler;
    private readonly ILogger<ProcessSandboxRunner> _logger;
    private readonly InProcessSandboxRunner _inProcess;
    private readonly object _lock = new();
    private ProcessSandboxRunner? _process;

    public SwitchableSandboxRunner(
        SandboxOptions options, CSharpScriptCompiler compiler, ILogger<ProcessSandboxRunner> logger)
    {
        _options = options;
        _compiler = compiler;
        _logger = logger;
        _inProcess = new InProcessSandboxRunner(compiler);
    }

    public Task<LegacyNodeResult> RunAsync(
        string cacheKey,
        string source,
        int timeoutSeconds,
        NodeExecutionContext context,
        IHttpClientFactory httpClientFactory,
        ICredentialAccessor credentialAccessor,
        ILogger logger,
        IReadOnlyDictionary<string, JsonElement>? extraInputs,
        IReadOnlyDictionary<Type, object?>? knownServices,
        CancellationToken cancellationToken)
    {
        var runner = _options.Mode == SandboxMode.Process ? GetProcessRunner() : (ISandboxRunner)_inProcess;
        return runner.RunAsync(cacheKey, source, timeoutSeconds, context, httpClientFactory,
            credentialAccessor, logger, extraInputs, knownServices, cancellationToken);
    }

    private ProcessSandboxRunner GetProcessRunner()
    {
        if (_process is { } existing)
        {
            return existing;
        }
        lock (_lock)
        {
            return _process ??= new ProcessSandboxRunner(_compiler, _options, _logger);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { } process)
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Registered as a DI singleton: a service that is IAsyncDisposable-only makes a *synchronous*
    // container Dispose() throw (the app disposes async, but some hosts/tests dispose sync). Implement
    // IDisposable too and bridge to the async path. In the common InProcess case _process is null, so
    // this is a no-op; only a live worker pool needs the (best-effort, shutdown-time) blocking wait.
    public void Dispose()
    {
        if (_process is { } process)
        {
            process.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
