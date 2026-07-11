using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KnotGarden.Features.Ai.Providers;

/// <summary>The inputs one chat completion needs, after config + key have been resolved.</summary>
public sealed record LlmChatRequest(
    string SystemPrompt,
    string UserMessage,
    AiProviderConfig Config,
    string ApiKey,
    int MaxTokens);

/// <summary>
/// A vendor adapter: turns a system+user prompt into the vendor's chat API call and returns the model's
/// text. Only the transport differs between vendors (endpoint, auth header, request/response shape) — the
/// prompt building and the workflow-JSON parsing around it stay vendor-agnostic. Non-2xx responses throw
/// (a transport/config failure the repair loop can't fix); the caller surfaces it as a failed job.
/// </summary>
public interface ILlmChatProvider
{
    /// <summary>The <see cref="LlmVendors"/> key this adapter handles.</summary>
    string Vendor { get; }

    Task<string> CompleteAsync(LlmChatRequest request, CancellationToken cancellationToken);
}

/// <summary>Shared POST-JSON-and-read helper so every provider treats HTTP failures identically.</summary>
internal static class LlmHttp
{
    public static async Task<string> SendAsync(
        IHttpClientFactory clientFactory,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Reuse the egress-policed client so provider calls obey the same allowlist/SSRF rules as node HTTP.
        var client = clientFactory.CreateClient("HttpNode");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var host = request.RequestUri?.Host ?? "LLM provider";
            throw new InvalidOperationException($"{host} returned HTTP {(int)response.StatusCode}: {Truncate(body, 500)}");
        }
        return body;
    }

    public static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
