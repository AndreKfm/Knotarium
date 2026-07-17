// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Features.Ai.Providers;

/// <summary>OpenAI (ChatGPT) Chat Completions adapter (Bearer auth). Works for any OpenAI-compatible endpoint.</summary>
public sealed class OpenAiChatProvider : ILlmChatProvider
{
    private const string DefaultBaseUrl = "https://api.openai.com";

    private readonly IHttpClientFactory _clientFactory;

    public OpenAiChatProvider(IHttpClientFactory clientFactory) => _clientFactory = clientFactory;

    public string Vendor => LlmVendors.OpenAi;

    public async Task<string> CompleteAsync(LlmChatRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(request.Config.BaseUrl) ? DefaultBaseUrl : request.Config.BaseUrl!.TrimEnd('/');
        // Current OpenAI models reject the legacy 'max_tokens' and require 'max_completion_tokens'.
        var body = OpenAiCompatible.BuildBody(request.Config.Model, request.SystemPrompt, request.UserMessage, request.MaxTokens, "max_completion_tokens");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

        var responseBody = await LlmHttp.SendAsync(_clientFactory, httpRequest, cancellationToken);
        return OpenAiCompatible.ExtractText(responseBody);
    }
}
