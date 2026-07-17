// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts.Ai;

/// <summary>
/// One chat completion against the instance's configured AI provider. <see cref="Model"/> and
/// <see cref="MaxTokens"/> optionally override the configured defaults for this single call; null
/// means "use what Settings → AI Provider says".
/// </summary>
public sealed record ChatCompletionRequest(
    string SystemPrompt,
    string UserMessage,
    string? Model = null,
    int? MaxTokens = null);

/// <summary>
/// The runtime seam for "send a prompt to the configured LLM, get its text back", consumed by the
/// AI nodes. Lives in Core (like <see cref="IWorkflowGenerator"/>) so the node slice never references
/// the AI feature slice directly. Not-configured / unknown-vendor / unresolved-key / transport
/// failures all throw <see cref="System.InvalidOperationException"/> — callers surface them as a
/// failed node, they are not repairable by re-prompting.
/// </summary>
public interface IChatCompletionService
{
    Task<string> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default);
}
