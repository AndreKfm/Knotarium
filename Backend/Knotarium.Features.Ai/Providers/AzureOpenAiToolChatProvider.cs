using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.Ai;

namespace Knotarium.Features.Ai.Providers;

/// <summary>
/// Azure OpenAI / Microsoft Copilot tool-calling adapter. OpenAI-compatible in body/response, but the model
/// is a <em>deployment name</em> in the URL, auth is the <c>api-key</c> header, and an <c>api-version</c>
/// query parameter is required. <see cref="AiProviderConfig.BaseUrl"/> must be the Azure resource endpoint.
/// </summary>
public sealed class AzureOpenAiToolChatProvider : ILlmToolChatProvider
{
    private const string DefaultApiVersion = "2024-06-01";

    private readonly IHttpClientFactory _clientFactory;

    public AzureOpenAiToolChatProvider(IHttpClientFactory clientFactory) => _clientFactory = clientFactory;

    public string Vendor => LlmVendors.Azure;

    public async Task<AgentTurnResult> CompleteTurnAsync(LlmToolChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Config.BaseUrl))
        {
            throw new InvalidOperationException("Azure OpenAI requires a Base URL (your resource endpoint, e.g. https://<resource>.openai.azure.com).");
        }

        var baseUrl = request.Config.BaseUrl!.TrimEnd('/');
        var apiVersion = string.IsNullOrWhiteSpace(request.Config.ApiVersion) ? DefaultApiVersion : request.Config.ApiVersion;
        var deployment = Uri.EscapeDataString(request.Config.Model);
        var url = $"{baseUrl}/openai/deployments/{deployment}/chat/completions?api-version={Uri.EscapeDataString(apiVersion!)}";

        // Model is carried by the URL (the deployment), so it is omitted from the body (null).
        var body = OpenAiCompatibleTools.BuildBody(
            null, request.SystemPrompt, request.Messages, request.Tools, request.MaxTokens, "max_tokens");

        return await OpenAiCompatibleTools.SendWithReasoningFallbackAsync(
            _clientFactory, body, request.Config.Model,
            b =>
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = OpenAiCompatibleTools.JsonContent(b) };
                httpRequest.Headers.Add("api-key", request.ApiKey);
                return httpRequest;
            },
            cancellationToken);
    }
}
