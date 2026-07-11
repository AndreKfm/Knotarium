using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Features.Ai.Providers;
using Xunit;

namespace KnotGarden.Tests.Ai;

public class LlmProvidersTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseBody;
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        public CapturingHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_responseBody) };
        }
    }

    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public FakeFactory(HttpMessageHandler handler) => _client = new HttpClient(handler);
        public HttpClient CreateClient(string name) => _client;
    }

    private static LlmChatRequest Req(AiProviderConfig config) =>
        new("SYSTEM", "USER", config, "sk-key", 4096);

    [Fact]
    public async Task Anthropic_PostsMessagesEndpoint_WithApiKeyHeader_AndExtractsText()
    {
        var handler = new CapturingHandler("""{ "content": [ { "type": "text", "text": "HELLO" } ] }""");
        var provider = new AnthropicChatProvider(new FakeFactory(handler));
        var config = new AiProviderConfig(LlmVendors.Anthropic, "claude-opus-4-8", "cred-1");

        var text = await provider.CompleteAsync(Req(config), CancellationToken.None);

        Assert.Equal("HELLO", text);
        Assert.EndsWith("/v1/messages", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.True(handler.Request.Headers.Contains("x-api-key"));
        Assert.True(handler.Request.Headers.Contains("anthropic-version"));
        Assert.Contains("\"system\"", handler.RequestBody);
    }

    [Fact]
    public async Task OpenAi_PostsChatCompletions_WithBearer_AndExtractsChoice()
    {
        var handler = new CapturingHandler("""{ "choices": [ { "message": { "content": "HELLO" } } ] }""");
        var provider = new OpenAiChatProvider(new FakeFactory(handler));
        var config = new AiProviderConfig(LlmVendors.OpenAi, "gpt-4o", "cred-1");

        var text = await provider.CompleteAsync(Req(config), CancellationToken.None);

        Assert.Equal("HELLO", text);
        Assert.EndsWith("/v1/chat/completions", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Contains("\"role\":\"system\"", handler.RequestBody);
        // Current OpenAI models require max_completion_tokens, not the legacy max_tokens.
        Assert.Contains("max_completion_tokens", handler.RequestBody);
        Assert.DoesNotContain("\"max_tokens\"", handler.RequestBody);
    }

    [Fact]
    public async Task Azure_BuildsDeploymentUrl_WithApiKeyHeader_AndApiVersion()
    {
        var handler = new CapturingHandler("""{ "choices": [ { "message": { "content": "HELLO" } } ] }""");
        var provider = new AzureOpenAiChatProvider(new FakeFactory(handler));
        var config = new AiProviderConfig(LlmVendors.Azure, "my-deploy", "cred-1", BaseUrl: "https://res.openai.azure.com");

        var text = await provider.CompleteAsync(Req(config), CancellationToken.None);

        Assert.Equal("HELLO", text);
        Assert.Contains("/openai/deployments/my-deploy/chat/completions", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Contains("api-version=", handler.Request.RequestUri.AbsoluteUri);
        Assert.True(handler.Request.Headers.Contains("api-key"));
        // Azure's conservative default api-version still uses max_tokens.
        Assert.Contains("max_tokens", handler.RequestBody);
    }

    [Fact]
    public async Task Azure_WithoutBaseUrl_Throws()
    {
        var provider = new AzureOpenAiChatProvider(new FakeFactory(new CapturingHandler("{}")));
        var config = new AiProviderConfig(LlmVendors.Azure, "my-deploy", "cred-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(Req(config), CancellationToken.None));
    }

    [Fact]
    public async Task Gemini_PostsGenerateContent_WithGoogleKeyHeader_NotUrl()
    {
        var handler = new CapturingHandler("""{ "candidates": [ { "content": { "parts": [ { "text": "HELLO" } ] } } ] }""");
        var provider = new GeminiChatProvider(new FakeFactory(handler));
        var config = new AiProviderConfig(LlmVendors.Gemini, "gemini-2.0-flash", "cred-1");

        var text = await provider.CompleteAsync(Req(config), CancellationToken.None);

        Assert.Equal("HELLO", text);
        Assert.Contains("/v1beta/models/gemini-2.0-flash:generateContent", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.True(handler.Request.Headers.Contains("x-goog-api-key"));
        Assert.DoesNotContain("sk-key", handler.Request.RequestUri.AbsoluteUri); // key never in the URL
    }

    [Fact]
    public async Task NonSuccessStatus_Throws()
    {
        var handler = new CapturingHandler("""{ "error": "bad" }""", HttpStatusCode.Unauthorized);
        var provider = new OpenAiChatProvider(new FakeFactory(handler));
        var config = new AiProviderConfig(LlmVendors.OpenAi, "gpt-4o", "cred-1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(Req(config), CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }
}
