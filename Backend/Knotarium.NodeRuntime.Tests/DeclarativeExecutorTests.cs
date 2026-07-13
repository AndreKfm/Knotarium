using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.NodeRuntime;

namespace Knotarium.NodeRuntime.Tests;

public class DeclarativeExecutorTests
{
    private class FakeWorkflowState : IWorkflowState
    {
        public Dictionary<string, object?> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(NodeId, string), JsonElement> Outputs { get; } = new();

        public T? GetVariable<T>(string name)
        {
            if (Variables.TryGetValue(name, out var val))
            {
                return (T?)val;
            }
            return default;
        }

        public void SetVariable(string name, object? value)
        {
            Variables[name] = value;
        }

        public JsonElement? GetNodeOutput(NodeId nodeId, string outputName)
        {
            if (Outputs.TryGetValue((nodeId, outputName), out var je))
            {
                return je;
            }
            return null;
        }
    }

    private class FakeHttpClient : IHttpClient
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        private readonly HttpResponseMessage _response;

        public FakeHttpClient(HttpResponseMessage response)
        {
            _response = response;
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_response);
        }
    }

    private class FakeCredentialAccessor : ICredentialAccessor
    {
        public Task<string?> GetSecretAsync(string credentialRef, CancellationToken cancellationToken = default)
        {
            if (credentialRef == "env:MY_SECRET")
            {
                return Task.FromResult<string?>("secret-123");
            }
            return Task.FromResult<string?>(null);
        }
    }

    private class FakeNodeContext : INodeContext
    {
        public ILogger Logger { get; } = NullLogger.Instance;
        public IWorkflowState State { get; }
        public IHttpClient? Http { get; }
        public ICredentialAccessor? Credentials { get; }

        public FakeNodeContext(IWorkflowState state, IHttpClient? http = null, ICredentialAccessor? credentials = null)
        {
            State = state;
            Http = http;
            Credentials = credentials;
        }
    }

    private NodePackageManifest CreateManifest(string id)
    {
        return new NodePackageManifest(
            new NodePackageId(id),
            "1.0.0",
            id,
            "category",
            NodeTier.Declarative,
            NodeSideEffectKind.IdempotentSideEffect,
            RecoveryMode.FailImmediately,
            10,
            new List<string>(),
            new List<ParameterDefinition>(),
            new List<OutputDefinition>()
        );
    }

    [Fact]
    public async Task ExecuteAsync_StartNode_ReturnsSuccess()
    {
        var manifest = CreateManifest("start");
        var executor = new DeclarativeExecutor(manifest);
        var state = new FakeWorkflowState();
        var context = new FakeNodeContext(state);
        var input = new NodeInput(new Dictionary<string, JsonElement>());

        var result = await executor.ExecuteAsync(input, context, CancellationToken.None);

        Assert.Equal(NodeExecutionStatus.Succeeded, result.Status);
        Assert.Equal("result", result.OutputName);
        Assert.Null(result.Payload);
    }

    [Fact]
    public async Task ExecuteAsync_EndNode_ReturnsSuccess()
    {
        var manifest = CreateManifest("end");
        var executor = new DeclarativeExecutor(manifest);
        var state = new FakeWorkflowState();
        var context = new FakeNodeContext(state);
        var input = new NodeInput(new Dictionary<string, JsonElement>());

        var result = await executor.ExecuteAsync(input, context, CancellationToken.None);

        Assert.Equal(NodeExecutionStatus.Succeeded, result.Status);
        Assert.Equal("", result.OutputName);
        Assert.Null(result.Payload);
    }

    [Fact]
    public async Task ExecuteAsync_LogNode_LogsAndReturnsSuccess()
    {
        var manifest = CreateManifest("log");
        var executor = new DeclarativeExecutor(manifest);
        var state = new FakeWorkflowState();
        var context = new FakeNodeContext(state);
        
        var parameters = new Dictionary<string, JsonElement>
        {
            ["message"] = JsonSerializer.SerializeToElement("Hello from Logger")
        };
        var input = new NodeInput(parameters);

        var result = await executor.ExecuteAsync(input, context, CancellationToken.None);

        Assert.Equal(NodeExecutionStatus.Succeeded, result.Status);
        Assert.Equal("result", result.OutputName);
        Assert.NotNull(result.Payload);

        var payloadDict = JsonSerializer.Deserialize<Dictionary<string, string>>(result.Payload.Value.GetRawText());
        Assert.NotNull(payloadDict);
        Assert.Equal("Hello from Logger", payloadDict["result"]);
    }

    [Fact]
    public async Task ExecuteAsync_SetVariableNode_SetsVariable()
    {
        var manifest = CreateManifest("setVariable");
        var executor = new DeclarativeExecutor(manifest);
        var state = new FakeWorkflowState();
        var context = new FakeNodeContext(state);

        var parameters = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement("myVar"),
            ["value"] = JsonSerializer.SerializeToElement("Alice")
        };
        var input = new NodeInput(parameters);

        var result = await executor.ExecuteAsync(input, context, CancellationToken.None);

        Assert.Equal(NodeExecutionStatus.Succeeded, result.Status);
        Assert.Equal("Alice", state.Variables["myVar"]);
    }

    [Fact]
    public async Task ExecuteAsync_DelayNode_DelaysSuccessfully()
    {
        var manifest = CreateManifest("delay");
        var executor = new DeclarativeExecutor(manifest);
        var state = new FakeWorkflowState();
        var context = new FakeNodeContext(state);

        var parameters = new Dictionary<string, JsonElement>
        {
            ["delayMs"] = JsonSerializer.SerializeToElement(10)
        };
        var input = new NodeInput(parameters);

        var result = await executor.ExecuteAsync(input, context, CancellationToken.None);

        Assert.Equal(NodeExecutionStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_ConditionNode_EvaluatesTrue()
    {
        var manifest = CreateManifest("condition");
        var executor = new DeclarativeExecutor(manifest);
        var state = new FakeWorkflowState();
        var context = new FakeNodeContext(state);

        var parameters = new Dictionary<string, JsonElement>
        {
            ["left"] = JsonSerializer.SerializeToElement(5.0),
            ["operator"] = JsonSerializer.SerializeToElement("Equal"),
            ["right"] = JsonSerializer.SerializeToElement(5.0)
        };
        var input = new NodeInput(parameters);

        var result = await executor.ExecuteAsync(input, context, CancellationToken.None);

        Assert.Equal(NodeExecutionStatus.Succeeded, result.Status);
        Assert.Equal("true", result.OutputName);
    }

    [Fact]
    public async Task ExecuteAsync_ConditionNode_EvaluatesFalse()
    {
        var manifest = CreateManifest("condition");
        var executor = new DeclarativeExecutor(manifest);
        var state = new FakeWorkflowState();
        var context = new FakeNodeContext(state);

        var parameters = new Dictionary<string, JsonElement>
        {
            ["left"] = JsonSerializer.SerializeToElement("apple"),
            ["operator"] = JsonSerializer.SerializeToElement("Equal"),
            ["right"] = JsonSerializer.SerializeToElement("banana")
        };
        var input = new NodeInput(parameters);

        var result = await executor.ExecuteAsync(input, context, CancellationToken.None);

        Assert.Equal(NodeExecutionStatus.Succeeded, result.Status);
        Assert.Equal("false", result.OutputName);
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestNode_SendsRequestSuccessfully()
    {
        var manifest = CreateManifest("httpRequest");
        var executor = new DeclarativeExecutor(manifest);
        var state = new FakeWorkflowState();

        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"ok\"}")
        };
        var httpClient = new FakeHttpClient(httpResponse);
        var credentials = new FakeCredentialAccessor();
        var context = new FakeNodeContext(state, httpClient, credentials);

        var parameters = new Dictionary<string, JsonElement>
        {
            ["url"] = JsonSerializer.SerializeToElement("https://example.com/api"),
            ["method"] = JsonSerializer.SerializeToElement("POST"),
            ["body"] = JsonSerializer.SerializeToElement("{\"test\":true}"),
            ["apiKeySecretRef"] = JsonSerializer.SerializeToElement("env:MY_SECRET")
        };
        var input = new NodeInput(parameters);

        var result = await executor.ExecuteAsync(input, context, CancellationToken.None);

        Assert.Equal(NodeExecutionStatus.Succeeded, result.Status);
        Assert.Equal("success", result.OutputName);
        Assert.NotNull(httpClient.LastRequest);
        Assert.Equal("Bearer", httpClient.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("secret-123", httpClient.LastRequest.Headers.Authorization?.Parameter);
    }
}
