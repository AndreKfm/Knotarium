// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.Ai;

namespace Knotarium.Features.Ai.Providers;

/// <summary>The inputs one tool-enabled chat turn needs, after config + key have been resolved.</summary>
public sealed record LlmToolChatRequest(
    string SystemPrompt,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<AgentToolDefinition> Tools,
    AiProviderConfig Config,
    string ApiKey,
    int MaxTokens);

/// <summary>
/// A vendor adapter for the agent tool-use loop: turns the neutral transcript + tool definitions into the
/// vendor's tool-calling chat API call and maps the reply back to an <see cref="AgentTurnResult"/>. Sibling
/// of <see cref="ILlmChatProvider"/> (single-turn, text-only) — that one is insufficient for tool use, so
/// the agent node uses this richer contract. Non-2xx responses throw (a transport/config failure the loop
/// can't fix); the caller surfaces it as a failed node.
/// </summary>
public interface ILlmToolChatProvider
{
    /// <summary>The <see cref="LlmVendors"/> key this adapter handles.</summary>
    string Vendor { get; }

    Task<AgentTurnResult> CompleteTurnAsync(LlmToolChatRequest request, CancellationToken cancellationToken);
}
