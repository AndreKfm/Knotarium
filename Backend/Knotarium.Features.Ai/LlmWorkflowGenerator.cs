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
/// Vendor-agnostic <see cref="IWorkflowGenerator"/>. Resolves the active <see cref="AiProviderConfig"/>
/// (edited in the UI), resolves its API key through <c>ISecretResolver</c> (a credential id or <c>env:</c>
/// ref), dispatches the chat call to the matching <see cref="ILlmChatProvider"/>, and parses the reply into
/// a workflow. Prompt building and parsing are shared across vendors; only the provider differs.
///
/// Not-configured / unresolved-key / unknown-vendor / non-2xx all <b>throw</b> — they are not fixable by
/// re-prompting, and the orchestrator surfaces them as a failed job. Unparseable model output stays a
/// repairable <see cref="WorkflowGenerationAttempt.ParseError"/>.
/// </summary>
public sealed class LlmWorkflowGenerator : IWorkflowGenerator
{
    private readonly IAiProviderConfigStore _configStore;
    private readonly IEnumerable<ILlmChatProvider> _providers;
    private readonly ISecretResolver _secretResolver;
    private readonly AiGenerationOptions _options;

    public LlmWorkflowGenerator(
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

    public async Task<WorkflowGenerationAttempt> GenerateAsync(WorkflowGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var config = await _configStore.GetAsync(cancellationToken);
        if (config is null || !config.IsComplete)
        {
            throw new InvalidOperationException(
                "AI workflow generation is not configured. Choose a provider, model, and API-key credential in Settings → AI Provider.");
        }

        var provider = _providers.FirstOrDefault(p => p.Vendor == config.Vendor)
            ?? throw new InvalidOperationException($"No adapter is registered for AI vendor '{config.Vendor}'.");

        var apiKey = await _secretResolver.ResolveAsync(config.CredentialRef, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"The AI provider API key could not be resolved from '{config.CredentialRef}'. Check the credential in Settings → AI Provider.");
        }

        var systemPrompt = GenerationPromptBuilder.BuildSystemPrompt(request.Catalog);
        var userMessage = GenerationPromptBuilder.BuildUserMessage(request.Intent, request.PriorErrors, request.CurrentWorkflow);
        var maxTokens = config.MaxTokens ?? _options.MaxTokens;

        var rawText = await provider.CompleteAsync(
            new LlmChatRequest(systemPrompt, userMessage, config, apiKey!, maxTokens), cancellationToken);

        var (workflow, error) = GeneratedWorkflowMapper.TryParse(rawText);
        if (workflow is not null && request.CurrentWorkflow is not null)
        {
            // Refine: keep the original workflow's id so saving the result updates it in place rather than
            // creating a new workflow. (The parser always mints a fresh id; the model never emits one.)
            workflow = workflow with { Id = request.CurrentWorkflow.Id };
        }
        return new WorkflowGenerationAttempt(workflow, rawText, error);
    }
}
