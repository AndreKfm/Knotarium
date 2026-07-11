using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Contracts.Options;
using KnotGarden.Core.Contracts.OpenApi;
using KnotGarden.Core.Domain.OpenApi;
using KnotGarden.Features.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnotGarden.Tests.Options;

public class RestCollectionOptionsLoaderTests
{
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsync;
        public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
            => _sendAsync = sendAsync;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _sendAsync(request, cancellationToken);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public string? LastClientName { get; private set; }
        public FakeHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) { LastClientName = name; return _client; }
    }

    private sealed class FakeServerConfigStore : IServerConfigStore
    {
        private readonly ServerConfigInfo? _config;
        public FakeServerConfigStore(ServerConfigInfo? config) => _config = config;
        public Task<ServerConfigInfo> CreateAsync(ServerConfigInfo c, CancellationToken ct) => Task.FromResult(c);
        public Task<ServerConfigInfo> UpdateAsync(ServerConfigInfo c, CancellationToken ct) => Task.FromResult(c);
        public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<ServerConfigInfo?> GetAsync(string id, CancellationToken ct) => Task.FromResult(_config);
        public Task<IReadOnlyList<ServerConfigInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ServerConfigInfo>>(Array.Empty<ServerConfigInfo>());
    }

    private sealed class FakeCredentialAccessor : ICredentialAccessor
    {
        private readonly Dictionary<string, string> _creds;
        public FakeCredentialAccessor(Dictionary<string, string>? creds = null) => _creds = creds ?? new();
        public Task<string?> GetSecretAsync(string credentialRef, CancellationToken ct = default)
        {
            _creds.TryGetValue(credentialRef, out var val);
            return Task.FromResult<string?>(val);
        }
    }

    private static ServerConfigInfo MakeServerConfig(
        string id = "srv1", string baseUrl = "https://api.example.com", string? credentialRef = null,
        bool allowInsecure = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new ServerConfigInfo(id, "Test", baseUrl, new Dictionary<string, string>(), "bearer", credentialRef, now, now, allowInsecure);
    }

    private static (RestCollectionOptionsLoader Loader, FakeHttpClientFactory Factory) BuildLoaderWithFactory(
        FakeHttpMessageHandler handler, ServerConfigInfo? config, Dictionary<string, string>? creds = null)
    {
        var factory = new FakeHttpClientFactory(new HttpClient(handler));
        var loader = new RestCollectionOptionsLoader(
            factory,
            new FakeServerConfigStore(config),
            new FakeCredentialAccessor(creds),
            NullLogger<RestCollectionOptionsLoader>.Instance);
        return (loader, factory);
    }

    private static RestCollectionOptionsLoader BuildLoader(
        FakeHttpMessageHandler handler, ServerConfigInfo? config, Dictionary<string, string>? creds = null)
        => BuildLoaderWithFactory(handler, config, creds).Loader;

    private static OptionLoadContext Context(params (string Key, string Value)[] dependsOn)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (k, v) in dependsOn) dict[k] = v;
        return new OptionLoadContext("srv1", dict);
    }

    [Fact]
    public async Task LoadAsync_MapsJsonArrayToOptionItems()
    {
        Uri? requestedUri = null;
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            requestedUri = req.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    [
                      { "id": "res_7f3a", "name": "Front Office" },
                      { "id": "res_22b1", "name": "Warehouse" }
                    ]
                    """)
            });
        });

        var loader = BuildLoader(handler, MakeServerConfig());
        var result = await loader.LoadAsync(Context(("path", "locations")), CancellationToken.None);

        Assert.Equal("https://api.example.com/locations", requestedUri?.AbsoluteUri);
        Assert.Equal(2, result.Options.Count);
        Assert.Equal("Front Office", result.Options[0].Label);
        Assert.Equal("res_7f3a", result.Options[0].Value);
        Assert.Equal("Warehouse", result.Options[1].Label);
        Assert.Equal("res_22b1", result.Options[1].Value);
    }

    [Fact]
    public async Task LoadAsync_AppliesBearerCredential_NeverLeaksSecret()
    {
        string? authHeader = null;
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            authHeader = req.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            });
        });

        var loader = BuildLoader(
            handler,
            MakeServerConfig(credentialRef: "cred-1"),
            new Dictionary<string, string> { ["cred-1"] = "top-secret" });

        var result = await loader.LoadAsync(Context(("path", "things")), CancellationToken.None);

        Assert.Equal("Bearer top-secret", authHeader);
        Assert.Empty(result.Options);
    }

    [Fact]
    public async Task LoadAsync_ResolvesNestedCollectionField()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "data": { "items": [ { "id": "1", "name": "A" } ] } }""")
            }));

        var loader = BuildLoader(handler, MakeServerConfig());
        var result = await loader.LoadAsync(
            Context(("path", "things"), ("collectionField", "data.items")), CancellationToken.None);

        Assert.Single(result.Options);
        Assert.Equal("A", result.Options[0].Label);
    }

    [Fact]
    public async Task LoadAsync_RejectsAbsolutePath()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }));

        var loader = BuildLoader(handler, MakeServerConfig());
        await Assert.ThrowsAsync<OptionsLoadException>(() =>
            loader.LoadAsync(Context(("path", "https://evil.example.com/steal")), CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_SubstitutesCascadingPlaceholderFromDependsOn()
    {
        Uri? requestedUri = null;
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            requestedUri = req.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        });

        var loader = BuildLoader(handler, MakeServerConfig());
        await loader.LoadAsync(
            Context(("path", "stores/{storeId}/pets"), ("storeId", "s_42")),
            CancellationToken.None);

        Assert.Equal("https://api.example.com/stores/s_42/pets", requestedUri?.AbsoluteUri);
    }

    [Fact]
    public async Task LoadAsync_UnresolvedPlaceholder_ThrowsActionableError()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }));

        var loader = BuildLoader(handler, MakeServerConfig());
        var ex = await Assert.ThrowsAsync<OptionsLoadException>(() =>
            loader.LoadAsync(Context(("path", "stores/{storeId}/pets")), CancellationToken.None));
        Assert.Contains("storeId", ex.Message);
    }

    [Fact]
    public async Task LoadAsync_UsesInsecureClient_WhenServerConfigAllowsIt()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }));

        var (loader, factory) = BuildLoaderWithFactory(handler, MakeServerConfig(allowInsecure: true));
        await loader.LoadAsync(Context(("path", "things")), CancellationToken.None);

        Assert.Equal("InsecureHttp", factory.LastClientName);
    }

    [Fact]
    public async Task LoadAsync_UsesSecureClient_ByDefault()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }));

        var (loader, factory) = BuildLoaderWithFactory(handler, MakeServerConfig());
        await loader.LoadAsync(Context(("path", "things")), CancellationToken.None);

        Assert.Equal("HttpNode", factory.LastClientName);
    }

    [Fact]
    public async Task LoadAsync_UnreachableSystem_ThrowsOptionsLoadException()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            throw new HttpRequestException("connection refused"));

        var loader = BuildLoader(handler, MakeServerConfig());
        await Assert.ThrowsAsync<OptionsLoadException>(() =>
            loader.LoadAsync(Context(("path", "things")), CancellationToken.None));
    }
}
