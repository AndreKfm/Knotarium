using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Features.Ai.Providers;

/// <summary>Anthropic Messages API adapter (x-api-key + anthropic-version; system is a top-level field).</summary>
public sealed class AnthropicChatProvider : ILlmChatProvider
{
    private const string DefaultBaseUrl = "https://api.anthropic.com";
    private const string DefaultVersion = "2023-06-01";

    private readonly IHttpClientFactory _clientFactory;

    public AnthropicChatProvider(IHttpClientFactory clientFactory) => _clientFactory = clientFactory;

    public string Vendor => LlmVendors.Anthropic;

    public async Task<string> CompleteAsync(LlmChatRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(request.Config.BaseUrl) ? DefaultBaseUrl : request.Config.BaseUrl!.TrimEnd('/');
        var body = new
        {
            model = request.Config.Model,
            max_tokens = request.MaxTokens,
            system = request.SystemPrompt,
            messages = new[] { new { role = "user", content = request.UserMessage } },
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Add("x-api-key", request.ApiKey);
        httpRequest.Headers.Add("anthropic-version",
            string.IsNullOrWhiteSpace(request.Config.ApiVersion) ? DefaultVersion : request.Config.ApiVersion);

        var responseBody = await LlmHttp.SendAsync(_clientFactory, httpRequest, cancellationToken);
        return ExtractText(responseBody);
    }

    private static string ExtractText(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Anthropic response had no 'content' array.");
        }

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                && block.TryGetProperty("text", out var text))
            {
                sb.Append(text.GetString());
            }
        }
        return sb.ToString();
    }
}
