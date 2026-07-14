using System;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.Ai;
using Knotarium.Features.Ai;
using Knotarium.Features.Ai.Providers;
using Xunit;

namespace Knotarium.Tests.Ai;

public class ChatCompletionServiceTests
{
    private sealed class FakeConfigStore : IAiProviderConfigStore
    {
        private readonly AiProviderConfig? _config;
        public FakeConfigStore(AiProviderConfig? config) => _config = config;
        public Task<AiProviderConfig?> GetAsync(CancellationToken ct = default) => Task.FromResult(_config);
        public Task SetAsync(AiProviderConfig config, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingProvider : ILlmChatProvider
    {
        public LlmChatRequest? LastRequest { get; private set; }
        public string Vendor => LlmVendors.Anthropic;
        public Task<string> CompleteAsync(LlmChatRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult("reply");
        }
    }

    private sealed class FakeSecretResolver : ISecretResolver
    {
        private readonly string? _value;
        public FakeSecretResolver(string? value) => _value = value;
        public Task<string?> ResolveAsync(string secretRef, CancellationToken ct = default) => Task.FromResult(_value);
    }

    private static ChatCompletionService Build(
        AiProviderConfig? config, RecordingProvider? provider = null, string? apiKey = "sk-key", AiGenerationOptions? options = null)
        => new(
            new FakeConfigStore(config),
            new ILlmChatProvider[] { provider ?? new RecordingProvider() },
            new FakeSecretResolver(apiKey),
            options ?? new AiGenerationOptions());

    private static AiProviderConfig CompleteConfig(int? maxTokens = null) =>
        new(LlmVendors.Anthropic, "configured-model", "cred-1", MaxTokens: maxTokens);

    [Fact]
    public async Task NotConfigured_Throws()
    {
        var service = Build(config: null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteAsync(new ChatCompletionRequest("s", "u")));
        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public async Task IncompleteConfig_Throws()
    {
        var service = Build(new AiProviderConfig(LlmVendors.OpenAi, "gpt-4o", ""));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteAsync(new ChatCompletionRequest("s", "u")));
    }

    [Fact]
    public async Task UnknownVendor_NoAdapter_Throws()
    {
        var service = Build(new AiProviderConfig(LlmVendors.Gemini, "m", "cred-1"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteAsync(new ChatCompletionRequest("s", "u")));
        Assert.Contains("No adapter", ex.Message);
    }

    [Fact]
    public async Task UnresolvedKey_Throws()
    {
        var service = Build(CompleteConfig(), apiKey: null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteAsync(new ChatCompletionRequest("s", "u")));
        Assert.Contains("could not be resolved", ex.Message);
    }

    [Fact]
    public async Task HappyPath_UsesConfiguredModel_AndOptionsDefaultTokens()
    {
        var provider = new RecordingProvider();
        var service = Build(CompleteConfig(), provider, options: new AiGenerationOptions { MaxTokens = 1234 });

        var reply = await service.CompleteAsync(new ChatCompletionRequest("sys", "user"));

        Assert.Equal("reply", reply);
        Assert.NotNull(provider.LastRequest);
        Assert.Equal("sys", provider.LastRequest!.SystemPrompt);
        Assert.Equal("user", provider.LastRequest.UserMessage);
        Assert.Equal("configured-model", provider.LastRequest.Config.Model);
        Assert.Equal("sk-key", provider.LastRequest.ApiKey);
        Assert.Equal(1234, provider.LastRequest.MaxTokens);
    }

    [Fact]
    public async Task ModelOverride_ReplacesConfiguredModel()
    {
        var provider = new RecordingProvider();
        var service = Build(CompleteConfig(), provider);

        await service.CompleteAsync(new ChatCompletionRequest("s", "u", Model: "override-model"));

        Assert.Equal("override-model", provider.LastRequest!.Config.Model);
    }

    [Fact]
    public async Task MaxTokens_PrefersRequest_ThenConfig_ThenOptions()
    {
        var provider = new RecordingProvider();

        // Request override wins over both.
        var service = Build(CompleteConfig(maxTokens: 500), provider, options: new AiGenerationOptions { MaxTokens = 1000 });
        await service.CompleteAsync(new ChatCompletionRequest("s", "u", MaxTokens: 42));
        Assert.Equal(42, provider.LastRequest!.MaxTokens);

        // Config wins over options.
        await service.CompleteAsync(new ChatCompletionRequest("s", "u"));
        Assert.Equal(500, provider.LastRequest!.MaxTokens);
    }
}
