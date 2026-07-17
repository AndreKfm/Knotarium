// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Infrastructure.OpenApi;
using Xunit;

namespace Knotarium.Tests.OpenApi;

public class OAuthTokenCacheTests
{
    // -------------------------------------------------------------------------
    // Fakes
    // -------------------------------------------------------------------------

    private sealed class FakeHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new HttpClient(new DelegatingHandlerStub(_handler));
    }

    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(_handler(req));
    }

    private static System.Net.Http.IHttpClientFactory MakeFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new FakeHttpClientFactory(handler);

    private static HttpResponseMessage TokenResponse(string token, int expiresIn = 3600)
    {
        var json = JsonSerializer.Serialize(new { access_token = token, expires_in = expiresIn });
        return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetToken_FirstCall_FetchesFromEndpoint()
    {
        var callCount = 0;
        var cache = new InMemoryOAuthTokenCache(MakeFactory(_ =>
        {
            callCount++;
            return TokenResponse("tok-first");
        }));

        var token = await cache.GetTokenAsync("key1", "https://token.test/token", "cid", "csec",
            Array.Empty<string>());

        Assert.Equal("tok-first", token);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetToken_SecondCall_ReturnsCachedToken()
    {
        var callCount = 0;
        var cache = new InMemoryOAuthTokenCache(MakeFactory(_ =>
        {
            callCount++;
            return TokenResponse("tok-cached");
        }));

        await cache.GetTokenAsync("key2", "https://t.test/token", "cid", "csec", Array.Empty<string>());
        var token = await cache.GetTokenAsync("key2", "https://t.test/token", "cid", "csec", Array.Empty<string>());

        Assert.Equal("tok-cached", token);
        Assert.Equal(1, callCount); // only one HTTP call
    }

    [Fact]
    public async Task GetToken_ExpiredToken_RefetchesFromEndpoint()
    {
        var callCount = 0;
        // expiresIn=1 → ExpiresAt is ~1s from now, which is well within the 30s buffer → treated as expired
        var cache = new InMemoryOAuthTokenCache(MakeFactory(_ =>
        {
            callCount++;
            return TokenResponse($"tok-{callCount}", expiresIn: 1);
        }));

        await cache.GetTokenAsync("key3", "https://t.test/token", "c", "s", Array.Empty<string>());
        await cache.GetTokenAsync("key3", "https://t.test/token", "c", "s", Array.Empty<string>());

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task Invalidate_ThenGet_FetchesFromEndpoint()
    {
        var callCount = 0;
        var cache = new InMemoryOAuthTokenCache(MakeFactory(_ =>
        {
            callCount++;
            return TokenResponse("tok-new");
        }));

        await cache.GetTokenAsync("key4", "https://t.test/token", "c", "s", Array.Empty<string>());
        cache.Invalidate("key4");
        var token = await cache.GetTokenAsync("key4", "https://t.test/token", "c", "s", Array.Empty<string>());

        Assert.Equal("tok-new", token);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetToken_EndpointReturnsError_ThrowsException()
    {
        var cache = new InMemoryOAuthTokenCache(MakeFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("bad_request") }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetTokenAsync("key5", "https://t.test/token", "c", "s", Array.Empty<string>()));

        Assert.Contains("400", ex.Message);
    }
}
