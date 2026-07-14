using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.Ai;
using Knotarium.Features.Ai.Providers;

namespace Knotarium.Features.Ai;

/// <summary>
/// Vendor-agnostic <see cref="IChatCompletionService"/> for runtime AI nodes. Resolves the active
/// <see cref="AiProviderConfig"/> (edited in the UI), resolves its API key through
/// <see cref="ISecretResolver"/> (a credential id or <c>env:</c> ref), applies any per-call
/// model/max-tokens overrides, and dispatches to the matching <see cref="ILlmChatProvider"/>.
/// Mirrors <see cref="LlmWorkflowGenerator"/>'s resolution rules; kept separate so the generation
/// path and the node path can evolve (and message) independently.
/// </summary>
public sealed class ChatCompletionService : IChatCompletionService
{
    private readonly IAiProviderConfigStore _configStore;
    private readonly IEnumerable<ILlmChatProvider> _providers;
    private readonly ISecretResolver _secretResolver;
    private readonly AiGenerationOptions _options;

    public ChatCompletionService(
        IAiProviderConfigStore configStore,
        IEnumerable<ILlmChatProvider> providers,
        ISecretResolver secretResolver,
        AiGenerationOptions options)
    {
        _configStore = configStore;
        _providers = providers;
        _secretResolver = secretResolver;
        _options = options;
    }

    public async Task<string> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var config = await _configStore.GetAsync(cancellationToken);
        if (config is null || !config.IsComplete)
        {
            throw new InvalidOperationException(
                "The AI provider is not configured. Choose a provider, model, and API-key credential in Settings → AI Provider.");
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            config = config with { Model = request.Model! };
        }

        var provider = _providers.FirstOrDefault(p => p.Vendor == config.Vendor)
            ?? throw new InvalidOperationException($"No adapter is registered for AI vendor '{config.Vendor}'.");

        var apiKey = await _secretResolver.ResolveAsync(config.CredentialRef, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"The AI provider API key could not be resolved from '{config.CredentialRef}'. Check the credential in Settings → AI Provider.");
        }

        var maxTokens = request.MaxTokens ?? config.MaxTokens ?? _options.MaxTokens;

        return await provider.CompleteAsync(
            new LlmChatRequest(request.SystemPrompt, request.UserMessage, config, apiKey!, maxTokens), cancellationToken);
    }
}
