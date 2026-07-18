// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.NodeRuntime.Sandbox;

namespace Knotarium.SandboxWorker;

/// <summary>
/// The executor-facing <see cref="INodeContext"/> inside the worker: every member forwards
/// to the host over the pipe. Log entries are one-way; state, HTTP and secret lookups are
/// request/response callbacks correlated by id.
/// </summary>
internal sealed class ProxyNodeContext : INodeContext
{
    private readonly SandboxConnection _connection;
    private readonly CancellationToken _executionToken;

    public ProxyNodeContext(SandboxConnection connection, CancellationToken executionToken)
    {
        _connection = connection;
        _executionToken = executionToken;
        Logger = new PipeLogger(connection);
        State = new ProxyWorkflowState(this);
        Http = new ProxyHttpClient(this);
        Credentials = new ProxyCredentialAccessor(this);
    }

    public ILogger Logger { get; }
    public IWorkflowState State { get; }
    public IHttpClient? Http { get; }
    public ICredentialAccessor? Credentials { get; }
    // External signal providers are host plugins; nodes that declare that capability are not
    // scheduled out-of-process (the host falls back in-process for them).
    public IExternalSignalProvider? ExternalSignals => null;

    private async Task<SandboxMessage> CallbackAsync(SandboxMessage request, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_executionToken, cancellationToken);
        return await _connection.RequestAsync(
            request, SandboxMessageTypes.CallbackResult, linked.Token).ConfigureAwait(false);
    }

    private SandboxMessage Callback(SandboxMessage request)
        // The IWorkflowState contract is synchronous; block this pool thread on the RPC. The
        // read loop runs on its own thread, so the response can always arrive.
        => CallbackAsync(request, CancellationToken.None).GetAwaiter().GetResult();

    private static SandboxMessage NewCallback(string kind) => new()
    {
        Type = SandboxMessageTypes.Callback,
        Id = Guid.NewGuid().ToString("N"),
        CallbackKind = kind
    };

    private sealed class ProxyWorkflowState : IWorkflowState
    {
        private readonly ProxyNodeContext _owner;
        public ProxyWorkflowState(ProxyNodeContext owner) => _owner = owner;

        public T? GetVariable<T>(string name)
        {
            var response = _owner.Callback(NewCallback(SandboxCallbackKinds.GetVariable) with { Name = name });
            if (!response.Found || response.Value is null)
            {
                return default;
            }
            try
            {
                return response.Value.Value.Deserialize<T>();
            }
            catch (JsonException)
            {
                return default;
            }
        }

        public void SetVariable(string name, object? value)
        {
            JsonElement? serialized = value is null ? null : JsonSerializer.SerializeToElement(value);
            _owner.Callback(NewCallback(SandboxCallbackKinds.SetVariable) with { Name = name, Value = serialized });
        }

        public bool TryResolveVariable(string name, out object? value)
        {
            var response = _owner.Callback(NewCallback(SandboxCallbackKinds.TryResolveVariable) with { Name = name });
            value = response.Value;
            return response.Found;
        }

        public JsonElement? GetNodeOutput(NodeId nodeId, string outputName)
            // Mirrors the host-side contract for script nodes — no RPC needed to say no.
            => throw new NotSupportedException(
                "GetNodeOutput is not available inside sandboxed node code. " +
                "Reference upstream node outputs through the node's inputs or workflow variables instead.");
    }

    private sealed class ProxyHttpClient : IHttpClient
    {
        private readonly ProxyNodeContext _owner;
        public ProxyHttpClient(ProxyNodeContext owner) => _owner = owner;

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var wire = await SandboxHttpTranslator.ToWireAsync(request, cancellationToken).ConfigureAwait(false);
            var response = await _owner.CallbackAsync(
                NewCallback(SandboxCallbackKinds.HttpSend) with { HttpRequest = wire },
                cancellationToken).ConfigureAwait(false);

            if (response.Error is not null)
            {
                throw new HttpRequestException(response.Error);
            }
            return SandboxHttpTranslator.FromWire(response.HttpResponse
                ?? throw new HttpRequestException("Sandbox host returned no HTTP response."));
        }
    }

    private sealed class ProxyCredentialAccessor : ICredentialAccessor
    {
        private readonly ProxyNodeContext _owner;
        public ProxyCredentialAccessor(ProxyNodeContext owner) => _owner = owner;

        public async Task<string?> GetSecretAsync(string credentialRef, CancellationToken cancellationToken = default)
        {
            var response = await _owner.CallbackAsync(
                NewCallback(SandboxCallbackKinds.GetSecret) with { Name = credentialRef },
                cancellationToken).ConfigureAwait(false);
            return response.Found ? response.Value?.GetString() : null;
        }
    }

    /// <summary>Fire-and-forget log forwarding; a lost log line must never fail the execution.</summary>
    private sealed class PipeLogger : ILogger
    {
        private readonly SandboxConnection _connection;
        public PipeLogger(SandboxConnection connection) => _connection = connection;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += $" ({exception.GetType().Name}: {exception.Message})";
            }
            _ = _connection.SendAsync(new SandboxMessage
            {
                Type = SandboxMessageTypes.Log,
                LogLevel = logLevel.ToString(),
                LogMessage = message
            }, CancellationToken.None);
        }
    }
}
