// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.NodeRuntime.Sandbox;

namespace Knotarium.Features.Nodes.Sandbox;

/// <summary>
/// Runs user-authored node code in a pooled, OS-confined worker process. The source is
/// compiled host-side (cached, screened by the banned-API analyzer) and the emitted assembly
/// is shipped to a worker; host callbacks (log, state, HTTP, secrets) are served over the
/// pipe during execution. A node timeout first requests cooperative cancellation; after
/// <see cref="SandboxOptions.KillGraceSeconds"/> the worker process is killed outright —
/// the guarantee the in-process path fundamentally cannot make.
/// Executors whose constructors need host services fall back to the in-process runner,
/// because services cannot cross the process boundary.
/// </summary>
public sealed class ProcessSandboxRunner : ISandboxRunner, IAsyncDisposable
{
    private readonly CSharpScriptCompiler _compiler;
    private readonly InProcessSandboxRunner _fallback;
    private readonly SandboxWorkerPool _pool;
    private readonly SandboxOptions _options;
    private readonly ILogger<ProcessSandboxRunner> _logger;

    public ProcessSandboxRunner(
        CSharpScriptCompiler compiler,
        SandboxOptions options,
        ILogger<ProcessSandboxRunner> logger)
    {
        _compiler = compiler;
        _fallback = new InProcessSandboxRunner(compiler);
        _options = options;
        _logger = logger;
        _pool = new SandboxWorkerPool(options, logger);
    }

    public async Task<LegacyNodeResult> RunAsync(
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
        var (executorType, assemblyBytes) = _compiler.GetOrCompileWithBytes(cacheKey, source);

        // Service-injected executors (e.g. OpenAPI compiled nodes) can't run out-of-process:
        // their constructor dependencies are live host objects. Documented fallback.
        var needsServices = knownServices is { Count: > 0 }
            && executorType.GetConstructors().All(c => c.GetParameters().Length > 0);
        if (needsServices)
        {
            _logger.LogWarning(
                "Sandbox: executor '{Type}' requires host services and falls back to in-process execution.",
                executorType.Name);
            return await _fallback.RunAsync(cacheKey, source, timeoutSeconds, context, httpClientFactory,
                credentialAccessor, logger, extraInputs, knownServices, cancellationToken);
        }

        var worker = await _pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteOnWorkerAsync(
                worker, assemblyBytes, timeoutSeconds, context, httpClientFactory, credentialAccessor,
                logger, extraInputs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _pool.ReleaseAsync(worker).ConfigureAwait(false);
        }
    }

    private async Task<LegacyNodeResult> ExecuteOnWorkerAsync(
        SandboxWorkerHandle worker,
        byte[] assemblyBytes,
        int timeoutSeconds,
        NodeExecutionContext context,
        IHttpClientFactory httpClientFactory,
        ICredentialAccessor credentialAccessor,
        ILogger logger,
        IReadOnlyDictionary<string, JsonElement>? extraInputs,
        CancellationToken cancellationToken)
    {
        var executionId = Guid.NewGuid().ToString("N");
        var inputs = CSharpScriptCompiler.BuildInputs(context, extraInputs);
        var state = new CSharpScriptCompiler.TaskWorkflowState(context);
        var http = new CSharpScriptCompiler.TaskHttpClient(httpClientFactory);

        var resultSource = new TaskCompletionSource<SandboxMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        worker.MessageHandler = message => message.Type switch
        {
            SandboxMessageTypes.ExecuteResult when message.Id == executionId
                => Complete(resultSource, message),
            SandboxMessageTypes.Callback
                => ServeCallbackAsync(worker, message, state, http, credentialAccessor, cancellationToken),
            SandboxMessageTypes.Log
                => ForwardLog(logger, message),
            _ => Task.CompletedTask
        };

        // The node's own timeout is enforced worker-side (cooperative CancelAfter); the host
        // adds the hard deadline behind it: timeout + grace, then the process dies. A node with
        // no declared timeout still gets the configured ceiling — nothing runs unbounded here.
        if (timeoutSeconds <= 0)
        {
            timeoutSeconds = _options.MaxRunSeconds;
        }
        var hardDeadline = TimeSpan.FromSeconds(timeoutSeconds + _options.KillGraceSeconds);

        await worker.Connection.SendAsync(new SandboxMessage
        {
            Type = SandboxMessageTypes.Execute,
            Id = executionId,
            AssemblyBytes = assemblyBytes,
            Inputs = inputs,
            TimeoutSeconds = timeoutSeconds
        }, cancellationToken).ConfigureAwait(false);

        var hardTimeout = Task.Delay(hardDeadline, CancellationToken.None);
        // Completes (without throwing) when the host cancels the run; never completes otherwise.
        var cancelWatcher = cancellationToken.CanBeCanceled
            ? Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default)
            : new TaskCompletionSource().Task;

        var completed = await Task.WhenAny(resultSource.Task, hardTimeout, cancelWatcher).ConfigureAwait(false);

        if (completed == cancelWatcher)
        {
            // Host-side cancellation (run cancelled / shutdown): ask nicely once, then keep
            // waiting until the hard deadline.
            try
            {
                await worker.Connection.SendAsync(new SandboxMessage
                {
                    Type = SandboxMessageTypes.Cancel,
                    Id = executionId
                }, CancellationToken.None).ConfigureAwait(false);
                completed = await Task.WhenAny(resultSource.Task, hardTimeout).ConfigureAwait(false);
            }
            catch
            {
                worker.Kill();
                return new LegacyNodeResult.Failure("Execution cancelled.");
            }
        }

        if (completed == (Task)resultSource.Task)
        {
            return ToLegacyResult(await resultSource.Task.ConfigureAwait(false));
        }

        // Hard deadline: the worker did not come back — kill the whole process tree. This is
        // the boundary that catches while(true){} and runaway allocation.
        worker.Kill();
        _logger.LogWarning("Sandbox: worker {Pid} exceeded the hard deadline of {Deadline}s and was killed.",
            worker.Process.Id, hardDeadline.TotalSeconds);
        return new LegacyNodeResult.Failure(
            $"Sandboxed execution exceeded {timeoutSeconds}s and the worker process was terminated.");
    }

    private static Task Complete(TaskCompletionSource<SandboxMessage> source, SandboxMessage message)
    {
        source.TrySetResult(message);
        return Task.CompletedTask;
    }

    private static Task ForwardLog(ILogger logger, SandboxMessage message)
    {
        var level = Enum.TryParse<LogLevel>(message.LogLevel, out var parsed) ? parsed : LogLevel.Information;
        logger.Log(level, "[sandbox] {Message}", message.LogMessage);
        return Task.CompletedTask;
    }

    private async Task ServeCallbackAsync(
        SandboxWorkerHandle worker,
        SandboxMessage request,
        IWorkflowState state,
        IHttpClient http,
        ICredentialAccessor credentialAccessor,
        CancellationToken cancellationToken)
    {
        SandboxMessage response;
        try
        {
            response = request.CallbackKind switch
            {
                SandboxCallbackKinds.GetVariable or SandboxCallbackKinds.TryResolveVariable
                    => ResolveVariable(request, state),
                SandboxCallbackKinds.SetVariable => SetVariable(request, state),
                SandboxCallbackKinds.HttpSend
                    => await ServeHttpAsync(request, http, credentialAccessor, cancellationToken).ConfigureAwait(false),
                SandboxCallbackKinds.GetSecret
                    => await ServeSecretAsync(request, credentialAccessor, cancellationToken).ConfigureAwait(false),
                _ => Reply(request) with { Error = $"Unknown callback kind '{request.CallbackKind}'." }
            };
        }
        catch (Exception ex)
        {
            response = Reply(request) with { Error = ex.Message };
        }

        await worker.Connection.SendAsync(response, CancellationToken.None).ConfigureAwait(false);
    }

    private static SandboxMessage Reply(SandboxMessage request) => new()
    {
        Type = SandboxMessageTypes.CallbackResult,
        Id = request.Id
    };

    private static SandboxMessage ResolveVariable(SandboxMessage request, IWorkflowState state)
    {
        var found = state.TryResolveVariable(request.Name!, out var value);
        return Reply(request) with
        {
            Found = found,
            Value = found && value is not null ? JsonSerializer.SerializeToElement(value) : null
        };
    }

    private static SandboxMessage SetVariable(SandboxMessage request, IWorkflowState state)
    {
        state.SetVariable(request.Name!, request.Value);
        return Reply(request);
    }

    private async Task<SandboxMessage> ServeHttpAsync(
        SandboxMessage request, IHttpClient http, ICredentialAccessor credentialAccessor,
        CancellationToken cancellationToken)
    {
        var wire = request.HttpRequest
            ?? throw new InvalidOperationException("httpSend callback carried no request.");

        if (_options.ProxyCredentials)
        {
            // Model 1: replace {{knotarium-secret:ref}} placeholders with the real values right
            // before the request leaves the host — the worker never held the plaintext.
            wire = await SandboxCredentialSubstitutor.SubstituteAsync(wire, credentialAccessor, cancellationToken)
                .ConfigureAwait(false);
        }

        using var httpRequest = SandboxHttpTranslator.FromWire(wire);
        using var httpResponse = await http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var wireResponse = await SandboxHttpTranslator.ToWireAsync(httpResponse, cancellationToken).ConfigureAwait(false);

        var cap = (long)_options.MaxHttpResponseMb * 1024 * 1024;
        if ((wireResponse.ContentBytes?.LongLength ?? 0) > cap)
        {
            return Reply(request) with
            {
                Error = $"HTTP response body of {wireResponse.ContentBytes!.LongLength} bytes exceeds the " +
                        $"{_options.MaxHttpResponseMb} MB sandbox limit."
            };
        }

        return Reply(request) with { HttpResponse = wireResponse };
    }

    private async Task<SandboxMessage> ServeSecretAsync(
        SandboxMessage request, ICredentialAccessor credentialAccessor, CancellationToken cancellationToken)
    {
        var secret = await credentialAccessor.GetSecretAsync(request.Name!, cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            return Reply(request) with { Found = false };
        }

        // Model 1 (default): hand out an opaque placeholder instead of the plaintext; the HTTP
        // proxy substitutes it host-side. Found-ness still reflects the real lookup, so missing
        // refs behave exactly as before.
        var value = _options.ProxyCredentials
            ? SandboxCredentialSubstitutor.MakePlaceholder(request.Name!)
            : secret;
        return Reply(request) with
        {
            Found = true,
            Value = JsonSerializer.SerializeToElement(value)
        };
    }

    private static LegacyNodeResult ToLegacyResult(SandboxMessage message)
    {
        if (!Enum.TryParse<NodeExecutionStatus>(message.Status, out var status))
        {
            return new LegacyNodeResult.Failure(message.Error ?? "Sandbox returned an unrecognized status.");
        }
        if (status == NodeExecutionStatus.Failed && message.Payload is null && message.Error is not null)
        {
            return new LegacyNodeResult.Failure(message.Error);
        }
        return CSharpScriptCompiler.NormalizeNodeResult(
            new NodeResult(message.OutputName ?? "success", message.Payload, status));
    }

    public async ValueTask DisposeAsync() => await _pool.DisposeAsync().ConfigureAwait(false);
}
