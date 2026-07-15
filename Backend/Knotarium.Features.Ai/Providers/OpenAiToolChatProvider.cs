using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.Ai;

namespace Knotarium.Features.Ai.Providers;

/// <summary>OpenAI (ChatGPT) tool-calling adapter (Bearer auth). Works for any OpenAI-compatible endpoint
/// exposing <c>tools</c>/<c>tool_calls</c> (OpenAI, vLLM, Ollama, LM Studio) via <see cref="AiProviderConfig.BaseUrl"/>.</summary>
public sealed class OpenAiToolChatProvider : ILlmToolChatProvider
{
    private const string DefaultBaseUrl = "https://api.openai.com";

    private readonly IHttpClientFactory _clientFactory;

    public OpenAiToolChatProvider(IHttpClientFactory clientFactory) => _clientFactory = clientFactory;

    public string Vendor => LlmVendors.OpenAi;

    public async Task<AgentTurnResult> CompleteTurnAsync(LlmToolChatRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(request.Config.BaseUrl) ? DefaultBaseUrl : request.Config.BaseUrl!.TrimEnd('/');
        var body = OpenAiCompatibleTools.BuildBody(
            request.Config.Model, request.SystemPrompt, request.Messages, request.Tools, request.MaxTokens, "max_completion_tokens");

        return await OpenAiCompatibleTools.SendWithReasoningFallbackAsync(
            _clientFactory, body, request.Config.Model ?? string.Empty,
            b =>
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
                {
                    Content = OpenAiCompatibleTools.JsonContent(b),
                };
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
                return httpRequest;
            },
            cancellationToken);
    }
}
