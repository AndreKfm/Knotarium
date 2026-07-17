// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

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
/// Vendor-agnostic <see cref="IAgentChatService"/> for the runtime <c>aiAgent</c> node. Resolves the active
/// <see cref="AiProviderConfig"/>, resolves its API key through <see cref="ISecretResolver"/>, applies any
/// per-call model/max-tokens overrides, and dispatches one tool-enabled turn to the matching
/// <see cref="ILlmToolChatProvider"/>. Mirrors <see cref="ChatCompletionService"/>; separate so the
/// single-turn and tool-loop paths can evolve and message independently. Gemini has no tool adapter in v1,
/// so a Gemini config fails here with a clear, actionable message.
/// </summary>
public sealed class AgentChatService : IAgentChatService
{
    private readonly IAiProviderConfigStore _configStore;
    private readonly IEnumerable<ILlmToolChatProvider> _providers;
    private readonly ISecretResolver _secretResolver;
    private readonly AiGenerationOptions _options;

    public AgentChatService(
        IAiProviderConfigStore configStore,
        IEnumerable<ILlmToolChatProvider> providers,
        ISecretResolver secretResolver,
        AiGenerationOptions options)
    {
        _configStore = configStore;
        _providers = providers;
        _secretResolver = secretResolver;
        _options = options;
    }

    public async Task<AgentTurnResult> CompleteTurnAsync(AgentChatRequest request, CancellationToken cancellationToken = default)
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

        if (string.Equals(config.Vendor, LlmVendors.Gemini, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The AI Agent node does not support Gemini in this version. Configure Anthropic or an OpenAI-compatible provider in Settings → AI Provider.");
        }

        var provider = _providers.FirstOrDefault(p => p.Vendor == config.Vendor)
            ?? throw new InvalidOperationException($"No tool-calling adapter is registered for AI vendor '{config.Vendor}'.");

        var apiKey = await _secretResolver.ResolveAsync(config.CredentialRef, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"The AI provider API key could not be resolved from '{config.CredentialRef}'. Check the credential in Settings → AI Provider.");
        }

        var maxTokens = request.MaxTokens ?? config.MaxTokens ?? _options.MaxTokens;

        return await provider.CompleteTurnAsync(
            new LlmToolChatRequest(request.SystemPrompt, request.Messages, request.Tools, config, apiKey!, maxTokens),
            cancellationToken);
    }
}
