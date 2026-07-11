using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.OpenApi;
using Knotarium.Core.Domain.OpenApi;
using Knotarium.Features.OpenApi;
using Knotarium.Features.Polling;
using Xunit;

namespace Knotarium.Tests.Polling;

/// <summary>
/// Unit tests for <see cref="OpenApiOperationInvoker"/>: verifies auth headers are applied
/// from <see cref="ServerConfigInfo"/> and that non-success HTTP responses are thrown as
/// exceptions rather than silently returned as poll payload.
/// </summary>
public class OpenApiOperationInvokerTests
{
    // -------------------------------------------------------------------------
    // Fakes / stubs
    // -------------------------------------------------------------------------

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new HttpClient(_handler, disposeHandler: false);
    }

    private sealed class StubSecretResolver : ISecretResolver
    {
        private readonly Dictionary<string, string> _secrets;
        public StubSecretResolver(Dictionary<string, string> secrets) => _secrets = secrets;
        public Task<string?> ResolveAsync(string secretRef, CancellationToken ct = default)
        {
            _secrets.TryGetValue(secretRef, out var val);
            return Task.FromResult<string?>(val);
        }
    }

    private sealed class FakeSpecStore : IOpenApiSpecStore
    {
        private readonly ParsedSpec _spec;
        public FakeSpecStore(ParsedSpec spec) => _spec = spec;

        public Task<IReadOnlyList<ImportedSpec>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImportedSpec>>(new[] { _spec.Metadata });

        public Task<(ImportedSpec Spec, ParsedSpec Full)?> GetLatestAsync(OpenApiSpecId id, CancellationToken ct)
        {
            (ImportedSpec, ParsedSpec)? result = (_spec.Metadata, _spec);
            return Task.FromResult(result);
        }

        public Task<(ImportedSpec Spec, ParsedSpec Full)?> GetVersionAsync(OpenApiSpecId id, int v, CancellationToken ct)
        {
            (ImportedSpec, ParsedSpec)? result = (_spec.Metadata, _spec);
            return Task.FromResult(result);
        }

        public Task<ImportedSpec> SaveAsync(ParsedSpec spec, CancellationToken ct) =>
            Task.FromResult(spec.Metadata);

        public Task<IReadOnlyList<ImportedSpec>> GetVersionsAsync(OpenApiSpecId id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImportedSpec>>(Array.Empty<ImportedSpec>());

        public Task<ApiOperation?> GetOperationAsync(OpenApiSpecId id, string operationId, CancellationToken ct) =>
            Task.FromResult<ApiOperation?>(null);

        public Task<bool> DeleteAsync(OpenApiSpecId id, CancellationToken ct) =>
            Task.FromResult(false);
    }

    private sealed class FakeServerConfigStore : IServerConfigStore
    {
        private readonly ServerConfigInfo? _config;
        public FakeServerConfigStore(ServerConfigInfo? config) => _config = config;

        public Task<ServerConfigInfo?> GetAsync(string id, CancellationToken ct) =>
            Task.FromResult(_config);

        public Task<ServerConfigInfo> CreateAsync(ServerConfigInfo c, CancellationToken ct) =>
            Task.FromResult(c);

        public Task<ServerConfigInfo> UpdateAsync(ServerConfigInfo c, CancellationToken ct) =>
            Task.FromResult(c);

        public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<ServerConfigInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ServerConfigInfo>>(Array.Empty<ServerConfigInfo>());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static readonly ImportedSpec DefaultSpecMeta = new ImportedSpec(
        new OpenApiSpecId("spec-1"), "Test API", "1.0", "openapi3",
        Array.Empty<string>(), Array.Empty<string>(),
        DateTimeOffset.UtcNow, 1);

    private static ParsedSpec MakeSpec(string operationId, IReadOnlyList<SecurityScheme>? securitySchemes = null) =>
        new ParsedSpec(
            DefaultSpecMeta,
            new[] { new ApiOperation(operationId, "GET", "/items", null, Array.Empty<string>(), Array.Empty<ApiParameter>(), null, Array.Empty<string>()) },
            Array.Empty<ApiSchema>(),
            securitySchemes ?? Array.Empty<SecurityScheme>());

    private static ServerConfigInfo MakeServerConfig(
        string credentialRef = "cred-1",
        string securitySchemeType = "http_bearer",
        bool allowInsecure = false) =>
        new ServerConfigInfo(
            "srv-1", "Test Server", "https://api.test",
            new Dictionary<string, string>(),
            securitySchemeType,
            credentialRef,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            allowInsecure);

    private static OpenApiOperationInvoker CreateInvoker(
        ParsedSpec spec,
        ServerConfigInfo serverConfig,
        HttpMessageHandler handler,
        Dictionary<string, string>? secrets = null)
    {
        secrets ??= new Dictionary<string, string> { ["cred-1"] = "my-secret-token" };
        return new OpenApiOperationInvoker(
            new FakeSpecStore(spec),
            new FakeServerConfigStore(serverConfig),
            new StubFactory(handler),
            new StubSecretResolver(secrets),
            new OpenApiRequestAuthApplier());
    }

    // -------------------------------------------------------------------------
    // Auth tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task BearerAuth_SetsAuthorizationHeader()
    {
        var spec = MakeSpec("listItems");
        var serverConfig = MakeServerConfig("cred-1", "http_bearer");
        var stubHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"items\":[]}") });

        var invoker = CreateInvoker(spec, serverConfig, stubHandler,
            new Dictionary<string, string> { ["cred-1"] = "tok-abc123" });

        await invoker.InvokeAsync("srv-1", "listItems", null, CancellationToken.None);

        Assert.NotNull(stubHandler.LastRequest);
        var auth = stubHandler.LastRequest!.Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("tok-abc123", auth.Parameter);
    }

    [Fact]
    public async Task BasicAuth_SetsBase64EncodedAuthorizationHeader()
    {
        var spec = MakeSpec("listItems");
        var serverConfig = MakeServerConfig("cred-1", "http_basic");
        var stubHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{}") });

        // Secret format is "user:pass"
        var invoker = CreateInvoker(spec, serverConfig, stubHandler,
            new Dictionary<string, string> { ["cred-1"] = "alice:p@ssword" });

        await invoker.InvokeAsync("srv-1", "listItems", null, CancellationToken.None);

        var auth = stubHandler.LastRequest!.Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Basic", auth!.Scheme);

        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!));
        Assert.Equal("alice:p@ssword", decoded);
    }

    [Fact]
    public async Task ApiKeyHeader_SetsCustomHeader()
    {
        var secSchemes = new[] { new SecurityScheme("apiKey", "apiKey", null, "header", "X-My-Key", null) };
        var spec = MakeSpec("listItems", secSchemes);
        var serverConfig = MakeServerConfig("cred-1", "apiKey");
        var stubHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{}") });

        var invoker = CreateInvoker(spec, serverConfig, stubHandler,
            new Dictionary<string, string> { ["cred-1"] = "key-xyz" });

        await invoker.InvokeAsync("srv-1", "listItems", null, CancellationToken.None);

        Assert.True(stubHandler.LastRequest!.Headers.Contains("X-My-Key"));
        Assert.Equal("key-xyz", string.Join(",", stubHandler.LastRequest.Headers.GetValues("X-My-Key")));
    }

    [Fact]
    public async Task ApiKeyQuery_AppendsKeyToUrl()
    {
        var secSchemes = new[] { new SecurityScheme("apiKey", "apiKey", null, "query", "api_key", null) };
        var spec = MakeSpec("listItems", secSchemes);
        var serverConfig = MakeServerConfig("cred-1", "apiKey");
        var stubHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{}") });

        var invoker = CreateInvoker(spec, serverConfig, stubHandler,
            new Dictionary<string, string> { ["cred-1"] = "secret-key" });

        await invoker.InvokeAsync("srv-1", "listItems", null, CancellationToken.None);

        var requestUrl = stubHandler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("api_key=secret-key", requestUrl);
        // Auth header should NOT be set for query-based apiKey
        Assert.Null(stubHandler.LastRequest.Headers.Authorization);
    }

    [Fact]
    public async Task NoCredentialRef_SendsUnauthenticatedRequest()
    {
        var spec = MakeSpec("listItems");
        var serverConfig = new ServerConfigInfo(
            "srv-1", "Test Server", "https://api.test",
            new Dictionary<string, string>(),
            "http_bearer",
            null, // no credential ref
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var stubHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"items\":[]}") });
        var invoker = CreateInvoker(spec, serverConfig, stubHandler);

        var result = await invoker.InvokeAsync("srv-1", "listItems", null, CancellationToken.None);

        // Should succeed without auth (no error)
        Assert.Equal("{\"items\":[]}", result.Body);
        Assert.Null(stubHandler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task OAuth2_ThrowsNotSupportedException()
    {
        var spec = MakeSpec("listItems");
        var serverConfig = MakeServerConfig("cred-1", "oauth2");
        var stubHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{}") });

        var invoker = CreateInvoker(spec, serverConfig, stubHandler);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => invoker.InvokeAsync("srv-1", "listItems", null, CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // Status-code handling tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NonSuccessStatus_ThrowsHttpRequestException()
    {
        var spec = MakeSpec("listItems");
        var serverConfig = MakeServerConfig("cred-1", "http_bearer");
        var stubHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            { Content = new StringContent("{\"error\":\"Unauthorized\"}") });

        var invoker = CreateInvoker(spec, serverConfig, stubHandler,
            new Dictionary<string, string> { ["cred-1"] = "bad-token" });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => invoker.InvokeAsync("srv-1", "listItems", null, CancellationToken.None));

        Assert.Contains("401", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ErrorStatusCodes_ThrowRatherThanReturnBody(HttpStatusCode statusCode)
    {
        var spec = MakeSpec("listItems");
        var serverConfig = MakeServerConfig("cred-1", "http_bearer");
        var stubHandler = new StubHandler(_ => new HttpResponseMessage(statusCode)
            { Content = new StringContent("{\"error\":\"something went wrong\"}") });

        var invoker = CreateInvoker(spec, serverConfig, stubHandler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => invoker.InvokeAsync("srv-1", "listItems", null, CancellationToken.None));
    }

    [Fact]
    public async Task SuccessResponse_ReturnsBodyAndEtag()
    {
        var spec = MakeSpec("listItems");
        var serverConfig = MakeServerConfig("cred-1", "http_bearer");
        var stubHandler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"items\":[1,2,3]}") };
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-v1\"");
            return resp;
        });

        var invoker = CreateInvoker(spec, serverConfig, stubHandler,
            new Dictionary<string, string> { ["cred-1"] = "tok-good" });

        var result = await invoker.InvokeAsync("srv-1", "listItems", null, CancellationToken.None);

        Assert.Equal("{\"items\":[1,2,3]}", result.Body);
        Assert.Equal("\"etag-v1\"", result.ETag);
    }
}
