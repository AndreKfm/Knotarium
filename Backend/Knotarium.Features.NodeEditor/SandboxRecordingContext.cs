// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.NodeEditor;

/// <summary>
/// Records which capabilities (logging, http, credentials, ...) an executor draft
/// actually invokes, so the sandbox can flag invocations the manifest never declared.
/// </summary>
internal sealed class CapabilityRecorder
{
    private readonly HashSet<string> _invocations = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Invocations => _invocations;

    public void Record(string capability)
    {
        if (!string.IsNullOrWhiteSpace(capability))
        {
            _invocations.Add(capability.Trim().ToLowerInvariant());
        }
    }
}

/// <summary>
/// An <see cref="INodeContext"/> whose services are inert fakes that report every use
/// to a <see cref="CapabilityRecorder"/> instead of touching the outside world.
/// </summary>
internal sealed class RecordingNodeContext : INodeContext
{
    public ILogger Logger { get; }
    public IWorkflowState State { get; }
    public IHttpClient? Http { get; }
    public ICredentialAccessor? Credentials { get; }

    public RecordingNodeContext(CapabilityRecorder recorder)
    {
        Logger = new RecordingLogger(recorder);
        State = new RecordingWorkflowState();
        Http = new RecordingHttpClient(recorder);
        Credentials = new RecordingCredentialAccessor(recorder);
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly CapabilityRecorder _recorder;

        public RecordingLogger(CapabilityRecorder recorder)
        {
            _recorder = recorder;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _recorder.Record("logging");
        }
    }

    private sealed class RecordingWorkflowState : IWorkflowState
    {
        private readonly Dictionary<string, object?> _variables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(NodeId NodeId, string Output), JsonElement> _outputs = new();

        public T? GetVariable<T>(string name)
        {
            if (_variables.TryGetValue(name, out var value) && value is T typed)
            {
                return typed;
            }

            return default;
        }

        public void SetVariable(string name, object? value)
        {
            _variables[name] = value;
        }

        public JsonElement? GetNodeOutput(NodeId nodeId, string outputName)
        {
            return _outputs.TryGetValue((nodeId, outputName), out var output) ? output : null;
        }
    }

    private sealed class RecordingHttpClient : IHttpClient
    {
        private readonly CapabilityRecorder _recorder;

        public RecordingHttpClient(CapabilityRecorder recorder)
        {
            _recorder = recorder;
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _recorder.Record("http");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }

    private sealed class RecordingCredentialAccessor : ICredentialAccessor
    {
        private readonly CapabilityRecorder _recorder;

        public RecordingCredentialAccessor(CapabilityRecorder recorder)
        {
            _recorder = recorder;
        }

        public Task<string?> GetSecretAsync(string credentialRef, CancellationToken cancellationToken = default)
        {
            _recorder.Record("credentials");
            return Task.FromResult<string?>("sandbox-secret");
        }
    }
}
