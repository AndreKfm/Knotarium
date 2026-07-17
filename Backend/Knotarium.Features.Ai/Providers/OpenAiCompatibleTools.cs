// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.Ai;

namespace Knotarium.Features.Ai.Providers;

/// <summary>
/// Shared request body + response parsing for OpenAI's tool-calling Chat Completions shape, used by the
/// OpenAI adapter and the Azure OpenAI adapter (which differ only in endpoint + auth + token-param). The
/// neutral <see cref="AgentMessage"/> transcript maps onto <c>role</c>/<c>tool_calls</c>/<c>tool</c> messages;
/// tool arguments cross the wire as JSON <em>strings</em> (unlike Anthropic's objects) both ways.
/// </summary>
internal static class OpenAiCompatibleTools
{
    // OpenAI reasoning models (e.g. GPT-5 family) reject function tools on /v1/chat/completions unless
    // reasoning_effort is "none". We can't send reasoning_effort blindly — non-reasoning models (gpt-4o)
    // reject it as an unknown parameter — and can't detect a model's class by its (often custom) name. So
    // we adapt: send without it, and on the specific 400 retry once with reasoning_effort:"none", caching
    // the model so subsequent turns skip the wasted round-trip. Keeps everything on /chat/completions, so
    // Azure and local OpenAI-compatible runtimes stay supported.
    private static readonly ConcurrentDictionary<string, bool> ReasoningNoneModels = new(StringComparer.Ordinal);

    public static Dictionary<string, object> BuildBody(
        string? model,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        int maxTokens,
        string tokenParam)
    {
        var wire = new List<object> { new { role = "system", content = systemPrompt } };
        wire.AddRange(MapMessages(messages));

        var body = new Dictionary<string, object>
        {
            ["messages"] = wire,
            [tokenParam] = maxTokens,
        };
        if (model is not null)
        {
            body["model"] = model;
        }
        if (tools.Count > 0)
        {
            body["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.ParametersSchema },
            }).ToArray();
        }
        return body;
    }

    /// <summary>
    /// Sends a tool-calling chat completion, adapting to reasoning models: on the specific "function tools
    /// with reasoning_effort are not supported" 400, retries once with <c>reasoning_effort:"none"</c> and
    /// remembers the model. <paramref name="cacheKey"/> is the model (or Azure deployment) name; empty skips
    /// caching. <paramref name="buildRequest"/> builds a fresh request (URL + auth) from the — possibly
    /// mutated — body each attempt, since an <see cref="HttpRequestMessage"/> can only be sent once.
    /// </summary>
    public static async Task<AgentTurnResult> SendWithReasoningFallbackAsync(
        IHttpClientFactory clientFactory,
        Dictionary<string, object> body,
        string cacheKey,
        Func<Dictionary<string, object>, HttpRequestMessage> buildRequest,
        CancellationToken cancellationToken)
    {
        var client = clientFactory.CreateClient("HttpNode");

        if (!string.IsNullOrEmpty(cacheKey) && ReasoningNoneModels.ContainsKey(cacheKey))
        {
            body["reasoning_effort"] = "none";
        }

        var (status, responseBody) = await PostAsync(client, buildRequest, body, cancellationToken);

        if (status == HttpStatusCode.BadRequest
            && !body.ContainsKey("reasoning_effort")
            && responseBody.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(cacheKey))
            {
                ReasoningNoneModels[cacheKey] = true;
            }
            body["reasoning_effort"] = "none";
            (status, responseBody) = await PostAsync(client, buildRequest, body, cancellationToken);
        }

        if ((int)status is < 200 or >= 300)
        {
            var detail = LlmHttp.Truncate(responseBody, 400);
            // If we already applied reasoning_effort:"none" (i.e. this is a reasoning model) and tools are in
            // play but it still fails, the model can't do the function-tool loop on Chat Completions. Give the
            // operator an actionable message instead of a raw HTTP error — the AI Agent needs a tool-capable
            // model (full reasoning-model tool support via the /v1/responses API is a planned follow-up).
            if (body.ContainsKey("reasoning_effort") && body.ContainsKey("tools"))
            {
                throw new InvalidOperationException(
                    "the configured model does not support function tools on the OpenAI Chat Completions API "
                    + "(it looks like a reasoning model, e.g. the gpt-5 family). The AI Agent needs a tool-capable "
                    + "model — set a per-node model override such as gpt-4o or gpt-4.1, or choose a non-reasoning "
                    + $"model in Settings → AI Provider. (HTTP {(int)status}: {detail})");
            }
            throw new InvalidOperationException($"OpenAI returned HTTP {(int)status}: {detail}");
        }

        return ParseResponse(responseBody);
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(
        HttpClient client,
        Func<Dictionary<string, object>, HttpRequestMessage> buildRequest,
        Dictionary<string, object> body,
        CancellationToken cancellationToken)
    {
        using var request = buildRequest(body);
        using var response = await client.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response.StatusCode, text);
    }

    /// <summary>Serializes <paramref name="body"/> as the JSON content of a POST request (helper for the providers).</summary>
    public static StringContent JsonContent(Dictionary<string, object> body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static IEnumerable<object> MapMessages(IReadOnlyList<AgentMessage> messages)
    {
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case AgentRoles.Tool:
                    yield return new { role = "tool", tool_call_id = m.ToolCallId, content = m.ToolResultJson ?? string.Empty };
                    break;

                case AgentRoles.Assistant when m.ToolCalls is { Count: > 0 }:
                    yield return new
                    {
                        role = "assistant",
                        content = m.Text, // may be null; OpenAI accepts null content alongside tool_calls
                        tool_calls = m.ToolCalls.Select(tc => new
                        {
                            id = tc.Id,
                            type = "function",
                            function = new { name = tc.Name, arguments = tc.Arguments.GetRawText() },
                        }).ToArray(),
                    };
                    break;

                case AgentRoles.Assistant:
                    yield return new { role = "assistant", content = m.Text ?? string.Empty };
                    break;

                default: // user
                    yield return new { role = "user", content = m.Text ?? string.Empty };
                    break;
            }
        }
    }

    public static AgentTurnResult ParseResponse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Chat completion response had no 'choices'.");
        }

        var message = choices[0].GetProperty("message");
        string? finalText = message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String
            ? contentEl.GetString()
            : null;

        var toolCalls = new List<AgentToolCall>();
        if (message.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcs.EnumerateArray())
            {
                var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                var fn = tc.GetProperty("function");
                var name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                var argsRaw = fn.TryGetProperty("arguments", out var argEl) ? argEl.GetString() ?? "{}" : "{}";
                toolCalls.Add(new AgentToolCall(id, name, ParseArguments(argsRaw)));
            }
        }

        var (inputTokens, outputTokens) = ReadUsage(root);
        // When tool calls are present, an empty/whitespace content shouldn't masquerade as a final answer.
        if (toolCalls.Count > 0 && string.IsNullOrWhiteSpace(finalText))
        {
            finalText = null;
        }
        return new AgentTurnResult(finalText, toolCalls, inputTokens, outputTokens);
    }

    /// <summary>Vendors return tool arguments as a JSON string; parse to an object, tolerating malformed/empty.</summary>
    private static JsonElement ParseArguments(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
    }

    private static (int input, int output) ReadUsage(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var input = usage.TryGetProperty("prompt_tokens", out var it) && it.TryGetInt32(out var iv) ? iv : 0;
            var output = usage.TryGetProperty("completion_tokens", out var ot) && ot.TryGetInt32(out var ov) ? ov : 0;
            return (input, output);
        }
        return (0, 0);
    }
}
