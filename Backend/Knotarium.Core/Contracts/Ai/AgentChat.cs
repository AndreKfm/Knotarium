// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts.Ai;

/// <summary>Well-known roles on an <see cref="AgentMessage"/>.</summary>
public static class AgentRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

/// <summary>
/// A tool the agent may call, as described to the model. <see cref="ParametersSchema"/> is a JSON-schema
/// object ({ "type": "object", "properties": {…}, "required": […] }) built from the node's tool binding.
/// The name is <c>[a-zA-Z0-9_]{1,64}</c>; the description is the model's only guidance on when to use it.
/// </summary>
public sealed record AgentToolDefinition(string Name, string Description, JsonElement ParametersSchema);

/// <summary>A single tool call the model requested in one turn. <see cref="Arguments"/> is a JSON object.</summary>
public sealed record AgentToolCall(string Id, string Name, JsonElement Arguments);

/// <summary>
/// One message in the running agent transcript. A <see cref="AgentRoles.User"/>/<see cref="AgentRoles.Assistant"/>
/// message carries <see cref="Text"/> (and, for an assistant turn that called tools, <see cref="ToolCalls"/>);
/// a <see cref="AgentRoles.Tool"/> message carries the result of one prior call via <see cref="ToolCallId"/>
/// + <see cref="ToolResultJson"/>. Providers map this neutral shape onto their vendor wire format.
/// </summary>
public sealed record AgentMessage(
    string Role,
    string? Text = null,
    IReadOnlyList<AgentToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? ToolResultJson = null);

/// <summary>
/// The outcome of one model turn. Exactly one of the two intents is present: either the model produced a
/// <see cref="FinalText"/> answer (<see cref="ToolCalls"/> empty), or it requested one or more tool calls
/// (<see cref="FinalText"/> null). Token counts come from the vendor's usage block for budgeting/journaling.
/// </summary>
public sealed record AgentTurnResult(
    string? FinalText,
    IReadOnlyList<AgentToolCall> ToolCalls,
    int InputTokens,
    int OutputTokens);

/// <summary>
/// One tool-enabled chat turn against the instance's configured AI provider. Mirrors
/// <see cref="ChatCompletionRequest"/> but carries the full transcript and the tool definitions, since a
/// tool-use loop is inherently multi-turn. <see cref="Model"/>/<see cref="MaxTokens"/> override the
/// configured defaults for this call; null means "use Settings → AI Provider".
/// </summary>
public sealed record AgentChatRequest(
    string SystemPrompt,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<AgentToolDefinition> Tools,
    string? Model = null,
    int? MaxTokens = null);

/// <summary>
/// The runtime seam for one tool-enabled model turn, consumed by the <c>aiAgent</c> node. Lives in Core
/// (like <see cref="IChatCompletionService"/>) so the node slice never references the AI feature slice
/// directly. Resolves the configured provider/model/key and dispatches to the matching vendor adapter.
/// Not-configured / unknown-vendor / unsupported-vendor (e.g. Gemini in v1) / unresolved-key / transport
/// failures all throw <see cref="System.InvalidOperationException"/> — the node surfaces them as a failure;
/// they are not repairable by another loop iteration.
/// </summary>
public interface IAgentChatService
{
    Task<AgentTurnResult> CompleteTurnAsync(AgentChatRequest request, CancellationToken cancellationToken = default);
}
