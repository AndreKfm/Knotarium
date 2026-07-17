// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Contracts.Ai;
using Knotarium.Features.Ai;
using Knotarium.Features.Ai.Providers;
using Microsoft.Extensions.Configuration;

// .NET convention: DI registration extensions live in Microsoft.Extensions.DependencyInjection
// so callers get AddAiGeneration() without an extra using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the AI workflow-generation slice: bound options, the persisted provider-config store, the
/// per-vendor chat-provider adapters, and the generator/orchestrator. Generator + orchestrator are
/// scoped (they pull the scoped WorkflowCompiler + ISecretResolver); the vendor adapters are stateless
/// singletons. The host-side generation runner/queue/worker/job-store stay in the host for now.
/// </summary>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddAiGeneration(this IServiceCollection services, IConfiguration configuration)
    {
        // Options bind from the "Ai" config section (the API key is referenced indirectly via
        // ISecretResolver, never stored here in plaintext).
        var options = configuration
            .GetSection(AiGenerationOptions.SectionName)
            .Get<AiGenerationOptions>() ?? new AiGenerationOptions();
        services.AddSingleton(options);

        // The active provider (vendor/model/API-key credential) is edited in the UI and persisted as an
        // AppSetting; the generator dispatches to the matching vendor adapter.
        services.AddScoped<IAiProviderConfigStore, AiProviderConfigStore>();
        services.AddSingleton<ILlmChatProvider, AnthropicChatProvider>();
        services.AddSingleton<ILlmChatProvider, OpenAiChatProvider>();
        services.AddSingleton<ILlmChatProvider, AzureOpenAiChatProvider>();
        services.AddSingleton<ILlmChatProvider, GeminiChatProvider>();
        services.AddScoped<IWorkflowGenerator, LlmWorkflowGenerator>();
        services.AddScoped<WorkflowGenerationOrchestrator>();

        // Tool-calling adapters for the AI agent node's tool-use loop (no Gemini adapter in v1 — the agent
        // service rejects a Gemini config with a clear message).
        services.AddSingleton<ILlmToolChatProvider, AnthropicToolChatProvider>();
        services.AddSingleton<ILlmToolChatProvider, OpenAiToolChatProvider>();
        services.AddSingleton<ILlmToolChatProvider, AzureOpenAiToolChatProvider>();

        // Runtime chat completion for AI nodes (aiPrompt) + tool-enabled turns for the AI agent node.
        // Registered here — BEFORE AddBuiltInNodes runs in the host — so the node slice's TryAdd
        // not-configured fallbacks never win in a real host.
        services.AddScoped<IChatCompletionService, ChatCompletionService>();
        services.AddScoped<IAgentChatService, AgentChatService>();
        return services;
    }
}
