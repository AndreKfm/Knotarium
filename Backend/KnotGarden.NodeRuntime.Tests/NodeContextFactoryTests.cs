using System;
using System.Collections.Generic;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.NodeRuntime;

namespace KnotGarden.NodeRuntime.Tests;

public class NodeContextFactoryTests
{
    private class MockWorkflowState : IWorkflowState
    {
        public T? GetVariable<T>(string name) => default;
        public void SetVariable(string name, object? value) { }
        public System.Text.Json.JsonElement? GetNodeOutput(NodeId nodeId, string outputName) => null;
    }

    private class MockHttpClient : IHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private class MockCredentialAccessor : ICredentialAccessor
    {
        public Task<string?> GetSecretAsync(string credentialRef, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>("secret-value");
        }
    }

    [Fact]
    public void Create_WithNoCapabilities_InjectsStructuralNulls()
    {
        // Arrange
        var logger = NullLogger.Instance;
        var state = new MockWorkflowState();
        var baseHttp = new MockHttpClient();
        var baseCredentials = new MockCredentialAccessor();
        var capabilities = Array.Empty<string>();

        // Act
        var context = NodeContextFactory.Create(logger, state, baseHttp, baseCredentials, capabilities);

        // Assert
        Assert.NotNull(context.Logger);
        Assert.NotNull(context.State);
        Assert.Null(context.Http);
        Assert.Null(context.Credentials);
    }

    [Fact]
    public void Create_WithHttpCapability_InjectsHttp()
    {
        // Arrange
        var logger = NullLogger.Instance;
        var state = new MockWorkflowState();
        var baseHttp = new MockHttpClient();
        var baseCredentials = new MockCredentialAccessor();
        var capabilities = new[] { "http" };

        // Act
        var context = NodeContextFactory.Create(logger, state, baseHttp, baseCredentials, capabilities);

        // Assert
        Assert.NotNull(context.Http);
        Assert.Null(context.Credentials);
    }

    [Fact]
    public void Create_WithCredentialsCapability_InjectsCredentials()
    {
        // Arrange
        var logger = NullLogger.Instance;
        var state = new MockWorkflowState();
        var baseHttp = new MockHttpClient();
        var baseCredentials = new MockCredentialAccessor();
        var capabilities = new[] { "credentials" };

        // Act
        var context = NodeContextFactory.Create(logger, state, baseHttp, baseCredentials, capabilities);

        // Assert
        Assert.Null(context.Http);
        Assert.NotNull(context.Credentials);
    }

    [Fact]
    public void Create_WithAllCapabilities_InjectsBoth()
    {
        // Arrange
        var logger = NullLogger.Instance;
        var state = new MockWorkflowState();
        var baseHttp = new MockHttpClient();
        var baseCredentials = new MockCredentialAccessor();
        var capabilities = new[] { "http", "credentials" };

        // Act
        var context = NodeContextFactory.Create(logger, state, baseHttp, baseCredentials, capabilities);

        // Assert
        Assert.NotNull(context.Http);
        Assert.NotNull(context.Credentials);
    }
}
