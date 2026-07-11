using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KnotGarden.Features.Ai.Providers;

/// <summary>
/// Google Gemini adapter (generateContent). The key goes in the <c>x-goog-api-key</c> header — never the
/// URL — and system text is passed as <c>systemInstruction</c>.
/// </summary>
public sealed class GeminiChatProvider : ILlmChatProvider
{
    private const string DefaultBaseUrl = "https://generativelanguage.googleapis.com";

    private readonly IHttpClientFactory _clientFactory;

    public GeminiChatProvider(IHttpClientFactory clientFactory) => _clientFactory = clientFactory;

    public string Vendor => LlmVendors.Gemini;

    public async Task<string> CompleteAsync(LlmChatRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(request.Config.BaseUrl) ? DefaultBaseUrl : request.Config.BaseUrl!.TrimEnd('/');
        var model = Uri.EscapeDataString(request.Config.Model);
        var url = $"{baseUrl}/v1beta/models/{model}:generateContent";

        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = request.SystemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = request.UserMessage } } } },
            generationConfig = new { maxOutputTokens = request.MaxTokens },
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Add("x-goog-api-key", request.ApiKey);

        var responseBody = await LlmHttp.SendAsync(_clientFactory, httpRequest, cancellationToken);
        return ExtractText(responseBody);
    }

    private static string ExtractText(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Gemini response had no 'candidates'.");
        }

        var sb = new StringBuilder();
        if (candidates[0].TryGetProperty("content", out var content)
            && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text))
                {
                    sb.Append(text.GetString());
                }
            }
        }
        return sb.ToString();
    }
}
