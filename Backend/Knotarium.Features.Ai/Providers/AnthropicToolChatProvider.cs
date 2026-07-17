// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.Ai;

namespace Knotarium.Features.Ai.Providers;

/// <summary>
/// Anthropic Messages API adapter for the agent tool-use loop: native <c>tools</c> + <c>tool_use</c>/
/// <c>tool_result</c> content blocks. Consecutive <see cref="AgentRoles.Tool"/> transcript messages are
/// merged into one user message (Anthropic requires tool_result blocks to sit in a user turn).
/// </summary>
public sealed class AnthropicToolChatProvider : ILlmToolChatProvider
{
    private const string DefaultBaseUrl = "https://api.anthropic.com";
    private const string DefaultVersion = "2023-06-01";

    private readonly IHttpClientFactory _clientFactory;

    public AnthropicToolChatProvider(IHttpClientFactory clientFactory) => _clientFactory = clientFactory;

    public string Vendor => LlmVendors.Anthropic;

    public async Task<AgentTurnResult> CompleteTurnAsync(LlmToolChatRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(request.Config.BaseUrl) ? DefaultBaseUrl : request.Config.BaseUrl!.TrimEnd('/');

        var body = new Dictionary<string, object>
        {
            ["model"] = request.Config.Model,
            ["max_tokens"] = request.MaxTokens,
            ["system"] = request.SystemPrompt,
            ["messages"] = MapMessages(request.Messages),
        };
        if (request.Tools.Count > 0)
        {
            body["tools"] = request.Tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                input_schema = t.ParametersSchema,
            }).ToArray();
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Add("x-api-key", request.ApiKey);
        httpRequest.Headers.Add("anthropic-version",
            string.IsNullOrWhiteSpace(request.Config.ApiVersion) ? DefaultVersion : request.Config.ApiVersion);

        var responseBody = await LlmHttp.SendAsync(_clientFactory, httpRequest, cancellationToken);
        return ParseResponse(responseBody);
    }

    private static List<object> MapMessages(IReadOnlyList<AgentMessage> messages)
    {
        var result = new List<object>();
        var i = 0;
        while (i < messages.Count)
        {
            var m = messages[i];
            switch (m.Role)
            {
                case AgentRoles.Tool:
                    // Merge the run of consecutive tool results into a single user message.
                    var blocks = new List<object>();
                    while (i < messages.Count && messages[i].Role == AgentRoles.Tool)
                    {
                        blocks.Add(new
                        {
                            type = "tool_result",
                            tool_use_id = messages[i].ToolCallId,
                            content = messages[i].ToolResultJson ?? string.Empty,
                        });
                        i++;
                    }
                    result.Add(new { role = "user", content = blocks });
                    break;

                case AgentRoles.Assistant:
                    var content = new List<object>();
                    if (!string.IsNullOrEmpty(m.Text))
                    {
                        content.Add(new { type = "text", text = m.Text });
                    }
                    if (m.ToolCalls is not null)
                    {
                        foreach (var tc in m.ToolCalls)
                        {
                            content.Add(new { type = "tool_use", id = tc.Id, name = tc.Name, input = tc.Arguments });
                        }
                    }
                    result.Add(new { role = "assistant", content });
                    i++;
                    break;

                default: // user
                    result.Add(new { role = "user", content = m.Text ?? string.Empty });
                    i++;
                    break;
            }
        }
        return result;
    }

    private static AgentTurnResult ParseResponse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var text = new StringBuilder();
        var toolCalls = new List<AgentToolCall>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "text" && block.TryGetProperty("text", out var txt))
                {
                    text.Append(txt.GetString());
                }
                else if (type == "tool_use")
                {
                    var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                    var name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                    var input = block.TryGetProperty("input", out var inputEl)
                        ? inputEl.Clone()
                        : JsonDocument.Parse("{}").RootElement.Clone();
                    toolCalls.Add(new AgentToolCall(id, name, input));
                }
            }
        }

        var (inputTokens, outputTokens) = ReadUsage(root);
        var finalText = text.Length > 0 ? text.ToString() : null;
        return new AgentTurnResult(finalText, toolCalls, inputTokens, outputTokens);
    }

    private static (int input, int output) ReadUsage(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var input = usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var iv) ? iv : 0;
            var output = usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var ov) ? ov : 0;
            return (input, output);
        }
        return (0, 0);
    }
}
