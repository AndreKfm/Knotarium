using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KnotGarden.Features.Ai.Providers;

/// <summary>
/// Azure OpenAI / Microsoft Copilot adapter. OpenAI-compatible in body/response, but Azure-hosted: the
/// model is a <em>deployment name</em> carried in the URL, auth is the <c>api-key</c> header, and an
/// <c>api-version</c> query parameter is required. <see cref="AiProviderConfig.BaseUrl"/> must be the Azure
/// resource endpoint (e.g. <c>https://my-resource.openai.azure.com</c>).
/// </summary>
public sealed class AzureOpenAiChatProvider : ILlmChatProvider
{
    private const string DefaultApiVersion = "2024-06-01";

    private readonly IHttpClientFactory _clientFactory;

    public AzureOpenAiChatProvider(IHttpClientFactory clientFactory) => _clientFactory = clientFactory;

    public string Vendor => LlmVendors.Azure;

    public async Task<string> CompleteAsync(LlmChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Config.BaseUrl))
        {
            throw new InvalidOperationException("Azure OpenAI requires a Base URL (your resource endpoint, e.g. https://<resource>.openai.azure.com).");
        }

        var baseUrl = request.Config.BaseUrl!.TrimEnd('/');
        var apiVersion = string.IsNullOrWhiteSpace(request.Config.ApiVersion) ? DefaultApiVersion : request.Config.ApiVersion;
        var deployment = Uri.EscapeDataString(request.Config.Model);
        var url = $"{baseUrl}/openai/deployments/{deployment}/chat/completions?api-version={Uri.EscapeDataString(apiVersion!)}";

        // Model lives in the URL as the deployment, so it is omitted from the body (null). Azure's default/
        // conservative api-versions still take 'max_tokens' (newer preview versions added max_completion_tokens).
        var body = OpenAiCompatible.BuildBody(null, request.SystemPrompt, request.UserMessage, request.MaxTokens, "max_tokens");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Add("api-key", request.ApiKey);

        var responseBody = await LlmHttp.SendAsync(_clientFactory, httpRequest, cancellationToken);
        return OpenAiCompatible.ExtractText(responseBody);
    }
}
