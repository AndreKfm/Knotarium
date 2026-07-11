using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Xunit;

namespace Knotarium.Tests.Nodes;

public class HttpRequestNodeTaskTests
{
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsync;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        {
            _sendAsync = sendAsync;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _sendAsync(request, cancellationToken);
        }
    }

    private class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name)
        {
            return _client;
        }
    }

    private class FakeSecretResolver : ISecretResolver
    {
        private readonly Dictionary<string, string> _secrets;

        public FakeSecretResolver(Dictionary<string, string> secrets)
        {
            _secrets = secrets;
        }

        public Task<string?> ResolveAsync(string secretRef, CancellationToken cancellationToken = default)
        {
            _secrets.TryGetValue(secretRef, out var val);
            return Task.FromResult<string?>(val);
        }
    }

    [Fact]
    public async Task HttpNode_ExecutesGetRequestSuccessfully()
    {
        // Arrange
        var fakeHandler = new FakeHttpMessageHandler((req, token) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("https://api.test.com/users", req.RequestUri?.AbsoluteUri);
            
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"success\"}")
            };
            return Task.FromResult(response);
        });

        using var httpClient = new HttpClient(fakeHandler);
        var factory = new FakeHttpClientFactory(httpClient);
        var secrets = new FakeSecretResolver(new Dictionary<string, string>());
        var task = new HttpRequestNodeTask(factory, secrets);

        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("http-1"),
            Inputs: new Dictionary<string, object>
            {
                ["url"] = "https://api.test.com/users",
                ["method"] = "GET"
            },
            GlobalVariables: new Dictionary<string, object>()
        );

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        var successResult = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.NotNull(successResult.Outputs);
        Assert.Equal(200.0, successResult.Outputs["statusCode"]);
        Assert.Equal("{\"status\":\"success\"}", successResult.Outputs["body"]);
        Assert.Equal(true, successResult.Outputs["isSuccess"]);
    }

    [Fact]
    public async Task HttpNode_ResolvesAndInjectsAuthorizationHeaderSafely()
    {
        // Arrange
        var fakeHandler = new FakeHttpMessageHandler((req, token) =>
        {
            Assert.Equal("Bearer super-secret-key-999", req.Headers.Authorization?.ToString());
            
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("authenticated")
            };
            return Task.FromResult(response);
        });

        using var httpClient = new HttpClient(fakeHandler);
        var factory = new FakeHttpClientFactory(httpClient);
        var secrets = new FakeSecretResolver(new Dictionary<string, string>
        {
            ["secret:api-key"] = "super-secret-key-999"
        });
        var task = new HttpRequestNodeTask(factory, secrets);

        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("http-2"),
            Inputs: new Dictionary<string, object>
            {
                ["url"] = "https://api.test.com/secure",
                ["method"] = "POST",
                ["apiKeySecretRef"] = "secret:api-key"
            },
            GlobalVariables: new Dictionary<string, object>()
        );

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        var successResult = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.NotNull(successResult.Outputs);
        
        // Assert that the secret itself never appears in the output payload!
        Assert.False(successResult.Outputs.ContainsKey("apiKeySecretRef"));
        Assert.DoesNotContain("super-secret-key-999", successResult.Outputs.Values);
    }

    private static HttpRequestNodeTask TaskWithCapture(Action<HttpRequestMessage> capture, Dictionary<string, string> secrets)
    {
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            capture(req);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        });
        return new HttpRequestNodeTask(new FakeHttpClientFactory(new HttpClient(handler)), new FakeSecretResolver(secrets));
    }

    private static NodeExecutionContext Ctx(Dictionary<string, object> inputs) => new(
        WorkflowId: WorkflowDefinitionId.New(),
        ExecutionId: Guid.NewGuid(),
        NodeId: NodeId.Create("http"),
        Inputs: inputs,
        GlobalVariables: new Dictionary<string, object>());

    [Fact]
    public async Task HttpNode_BasicAuth_EncodesUsernameAndCredentialSecret()
    {
        HttpRequestMessage? seen = null;
        var task = TaskWithCapture(r => seen = r, new Dictionary<string, string> { ["cred:pw"] = "s3cret" });

        await task.ExecuteAsync(Ctx(new Dictionary<string, object>
        {
            ["url"] = "https://api.test.com/x",
            ["authType"] = "basic",
            ["authUsername"] = "alice",
            ["authCredentialRef"] = "cred:pw",
        }), CancellationToken.None);

        var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("alice:s3cret"));
        Assert.Equal("Basic", seen?.Headers.Authorization?.Scheme);
        Assert.Equal(expected, seen?.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task HttpNode_ApiKeyAuth_SendsCustomHeaderWithPrefix()
    {
        HttpRequestMessage? seen = null;
        var task = TaskWithCapture(r => seen = r, new Dictionary<string, string> { ["cred:key"] = "abc123" });

        await task.ExecuteAsync(Ctx(new Dictionary<string, object>
        {
            ["url"] = "https://api.test.com/x",
            ["authType"] = "apiKey",
            ["authHeaderName"] = "X-Api-Key",
            ["authValuePrefix"] = "Token ",
            ["authCredentialRef"] = "cred:key",
        }), CancellationToken.None);

        Assert.True(seen!.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("Token abc123", string.Join("", values!));
        Assert.Null(seen.Headers.Authorization); // api-key scheme must not touch Authorization
    }

    [Fact]
    public async Task HttpNode_AppliesCustomHeadersAndBody_FromManifestFields()
    {
        HttpRequestMessage? seen = null;
        string? sentBody = null;
        var handler = new FakeHttpMessageHandler(async (req, ct) =>
        {
            seen = req;
            sentBody = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });
        var task = new HttpRequestNodeTask(new FakeHttpClientFactory(new HttpClient(handler)), new FakeSecretResolver(new Dictionary<string, string>()));

        await task.ExecuteAsync(Ctx(new Dictionary<string, object>
        {
            ["url"] = "https://api.test.com/x",
            ["method"] = "POST",
            ["headers"] = "{\"X-Trace\": \"abc\", \"Accept\": \"application/json\"}",
            ["body"] = "{\"hello\":\"world\"}",
        }), CancellationToken.None);

        Assert.True(seen!.Headers.TryGetValues("X-Trace", out var trace));
        Assert.Equal("abc", string.Join("", trace!));
        Assert.Equal("{\"hello\":\"world\"}", sentBody);
    }

    [Fact]
    public async Task HttpNode_ParsesLineBasedHeaders()
    {
        HttpRequestMessage? seen = null;
        var task = TaskWithCapture(r => seen = r, new Dictionary<string, string>());

        await task.ExecuteAsync(Ctx(new Dictionary<string, object>
        {
            ["url"] = "https://api.test.com/x",
            ["headers"] = "X-One: 1\nX-Two: two",
        }), CancellationToken.None);

        Assert.True(seen!.Headers.TryGetValues("X-One", out var one) && string.Join("", one!) == "1");
        Assert.True(seen.Headers.TryGetValues("X-Two", out var two) && string.Join("", two!) == "two");
    }
}
