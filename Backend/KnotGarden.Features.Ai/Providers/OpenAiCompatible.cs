using System;
using System.Collections.Generic;
using System.Text.Json;

namespace KnotGarden.Features.Ai.Providers;

/// <summary>
/// Shared request body + response parsing for OpenAI's Chat Completions shape, used by both the OpenAI
/// adapter and the Azure OpenAI / Copilot adapter (which differ only in endpoint + auth header).
/// </summary>
internal static class OpenAiCompatible
{
    /// <summary>
    /// Build the chat-completions body. When <paramref name="model"/> is null the model is carried by the
    /// URL instead (Azure deployments), so it is omitted from the body. <paramref name="tokenParam"/> names
    /// the token-limit field: OpenAI's current models require <c>max_completion_tokens</c> (older ones and
    /// Azure's conservative api-versions still use <c>max_tokens</c>), so the caller picks the right one.
    /// </summary>
    public static object BuildBody(string? model, string systemPrompt, string userMessage, int maxTokens, string tokenParam)
    {
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userMessage },
        };

        var body = new Dictionary<string, object>
        {
            ["messages"] = messages,
            [tokenParam] = maxTokens,
        };
        if (model is not null) body["model"] = model;
        return body;
    }

    public static string ExtractText(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Chat completion response had no 'choices'.");
        }

        return choices[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }
}
