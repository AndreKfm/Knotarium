using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Features.Polling;
using Xunit;

namespace Knotarium.Tests.Polling;

public class HttpPollSourceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public HttpRequestMessage? LastRequest { get; private set; }
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
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

    private sealed class NullSecretResolver : ISecretResolver
    {
        public Task<string?> ResolveAsync(string secretRef, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static HttpPollSource CreateSource(HttpMessageHandler handler) =>
        new HttpPollSource(new StubFactory(handler), new NullSecretResolver());

    [Fact]
    public async Task Etag_304_ReportsNoNew()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var source = CreateSource(handler);
        var config = "{\"changeDetection\":\"etag\",\"url\":\"https://x.test/feed\",\"method\":\"GET\"}";

        var result = await source.PollAsync(new PollContext(config, Cursor: "\"abc\""), CancellationToken.None);

        Assert.False(result.HasNew);
        Assert.Equal("\"abc\"", handler.LastRequest!.Headers.IfNoneMatch.ToString());
    }

    [Fact]
    public async Task Etag_200_ReportsNewAndStoresEtag()
    {
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"v\":1}") };
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"new-etag\"");
            return resp;
        });
        var source = CreateSource(handler);
        var config = "{\"changeDetection\":\"etag\",\"url\":\"https://x.test/feed\",\"method\":\"GET\"}";

        var result = await source.PollAsync(new PollContext(config, Cursor: null), CancellationToken.None);

        Assert.True(result.HasNew);
        Assert.Equal("\"new-etag\"", result.NewCursor);
        Assert.Equal("{\"v\":1}", result.Payload);
    }

    [Fact]
    public async Task Hash_DelegatesToBodyDetector()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"v\":1}") });
        var source = CreateSource(handler);
        var config = "{\"changeDetection\":\"hash\",\"url\":\"https://x.test/feed\",\"method\":\"GET\"}";

        var first = await source.PollAsync(new PollContext(config, Cursor: null), CancellationToken.None);
        var second = await source.PollAsync(new PollContext(config, Cursor: first.NewCursor), CancellationToken.None);

        Assert.True(first.HasNew);
        Assert.False(second.HasNew);
    }

    [Fact]
    public async Task LastModified_200_StoresRfc1123Cursor_SentBackAsValidHttpDate()
    {
        var lastModified = new DateTimeOffset(2026, 6, 14, 4, 55, 0, TimeSpan.Zero);
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"v\":1}") };
            resp.Content.Headers.LastModified = lastModified;
            return resp;
        });
        var source = CreateSource(handler);
        var config = "{\"changeDetection\":\"last-modified\",\"url\":\"https://x.test/feed\",\"method\":\"GET\"}";

        var first = await source.PollAsync(new PollContext(config, Cursor: null), CancellationToken.None);

        // Cursor must be a valid RFC-1123 HTTP-date (round-trips through If-Modified-Since parsing).
        Assert.True(first.HasNew);
        Assert.Equal(lastModified.ToString("R"), first.NewCursor); // exact RFC-1123, no precision loss

        // Second poll: the stored cursor is sent verbatim as a parseable If-Modified-Since header.
        var second = await source.PollAsync(new PollContext(config, Cursor: first.NewCursor), CancellationToken.None);
        Assert.Equal(first.NewCursor, handler.LastRequest!.Headers.IfModifiedSince!.Value.ToString("R"));
        // Same Last-Modified returned => not new.
        Assert.False(second.HasNew);
    }
}
