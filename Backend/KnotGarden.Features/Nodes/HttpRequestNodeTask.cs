using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;

namespace KnotGarden.Features.Nodes;

public class HttpRequestNodeTask : INodeTask
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly ISecretResolver _secretResolver;
    private readonly IOutboundHttpTelemetry? _telemetry;

    public HttpRequestNodeTask(IHttpClientFactory clientFactory, ISecretResolver secretResolver, IOutboundHttpTelemetry? telemetry = null)
    {
        _clientFactory = clientFactory;
        _secretResolver = secretResolver;
        _telemetry = telemetry;
    }

    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        if (!context.Inputs.TryGetValue("url", out var urlObj) || urlObj == null || string.IsNullOrEmpty(urlObj.ToString()))
        {
            return new LegacyNodeResult.Failure("HTTP Request failed: Missing required 'url' input.");
        }

        var url = urlObj.ToString()!;
        var method = context.Inputs.TryGetValue("method", out var methodObj) ? methodObj?.ToString() ?? "GET" : "GET";

        var client = _clientFactory.CreateClient("HttpNode");
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        using var httpActivity = _telemetry != null && Uri.TryCreate(url, UriKind.Absolute, out var requestUri)
            ? _telemetry.StartOutboundHttpActivity(requestUri, method, context)
            : null;

        // Authentication — flexible scheme selected on the node (none / bearer / basic / api-key header),
        // with the secret pulled from a stored credential. Kept before custom headers so an explicit
        // Authorization header can still override it if the user really wants to.
        await ApplyAuthenticationAsync(request, context, cancellationToken);

        // Custom headers (JSON object or "Key: Value" lines). Content-Type is applied to the body below.
        var headers = ParseHeaders(context.Inputs.TryGetValue("headers", out var headersObj) ? headersObj?.ToString() : null);
        string? contentType = null;
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = header.Value;
                continue;
            }
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Body: the manifest field is "body"; older workflows used "payload", so accept either.
        object? bodyValue = null;
        if (context.Inputs.TryGetValue("body", out var bodyObj) && bodyObj != null && !string.IsNullOrEmpty(bodyObj.ToString()))
        {
            bodyValue = bodyObj;
        }
        else if (context.Inputs.TryGetValue("payload", out var payloadObj) && payloadObj != null)
        {
            bodyValue = payloadObj;
        }

        if (bodyValue != null)
        {
            var jsonBody = bodyValue is string strBody ? strBody : JsonSerializer.Serialize(bodyValue);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, contentType ?? "application/json");
        }

        try
        {
            var response = await client.SendAsync(request, cancellationToken);
            httpActivity?.SetTag("http.response.status_code", (int)response.StatusCode);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            var outputs = new Dictionary<string, object>
            {
                ["statusCode"] = (double)response.StatusCode,
                ["body"] = content,
                ["isSuccess"] = response.IsSuccessStatusCode
            };

            if (response.IsSuccessStatusCode)
            {
                return new LegacyNodeResult.Success(outputs);
            }
            else
            {
                return new LegacyNodeResult.Failure($"HTTP Request failed with status code {(int)response.StatusCode}. Content: {content}");
            }
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"HTTP Request encountered network exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies the node's configured authentication. The scheme is chosen via <c>authType</c>; the secret
    /// (token / password / api-key) is resolved from the credential referenced by <c>authCredentialRef</c>,
    /// so it is never stored in the workflow definition. Falls back to the legacy <c>apiKeySecretRef</c>
    /// (Bearer) input for workflows built before the flexible auth field existed.
    /// </summary>
    private async Task ApplyAuthenticationAsync(HttpRequestMessage request, NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var authType = (context.Inputs.TryGetValue("authType", out var authTypeObj) ? authTypeObj?.ToString() : null)?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(authType) || authType == "none")
        {
            // Back-compat: a bare "apiKeySecretRef" still means "Bearer <secret>".
            if (context.Inputs.TryGetValue("apiKeySecretRef", out var legacyRef) && legacyRef != null && !string.IsNullOrEmpty(legacyRef.ToString()))
            {
                var legacySecret = await _secretResolver.ResolveAsync(legacyRef.ToString()!, cancellationToken);
                if (!string.IsNullOrEmpty(legacySecret))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", legacySecret);
                }
            }
            return;
        }

        var credentialRef = context.Inputs.TryGetValue("authCredentialRef", out var credRefObj) ? credRefObj?.ToString() : null;
        var secret = !string.IsNullOrEmpty(credentialRef)
            ? await _secretResolver.ResolveAsync(credentialRef!, cancellationToken)
            : null;

        switch (authType)
        {
            case "bearer":
                if (!string.IsNullOrEmpty(secret))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                }
                break;

            case "basic":
            {
                var username = context.Inputs.TryGetValue("authUsername", out var userObj) ? userObj?.ToString() ?? string.Empty : string.Empty;
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{secret}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
                break;
            }

            case "apikey":
            {
                var headerName = context.Inputs.TryGetValue("authHeaderName", out var headerNameObj) ? headerNameObj?.ToString() : null;
                if (string.IsNullOrWhiteSpace(headerName))
                {
                    headerName = "X-API-Key";
                }
                var prefix = context.Inputs.TryGetValue("authValuePrefix", out var prefixObj) ? prefixObj?.ToString() ?? string.Empty : string.Empty;
                request.Headers.TryAddWithoutValidation(headerName!, prefix + (secret ?? string.Empty));
                break;
            }
        }
    }

    /// <summary>
    /// Parses the free-form "headers" field. Accepts either a JSON object (<c>{"X-Foo":"bar"}</c>) or
    /// newline-separated <c>Key: Value</c> lines, so users can paste whatever their API docs show.
    /// </summary>
    private static List<KeyValuePair<string, string>> ParseHeaders(string? raw)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? string.Empty : prop.Value.ToString();
                        result.Add(new KeyValuePair<string, string>(prop.Name, value));
                    }
                    return result;
                }
            }
            catch (JsonException)
            {
                // Not valid JSON — fall through to line parsing.
            }
        }

        foreach (var line in trimmed.Split('\n'))
        {
            var entry = line.Trim();
            if (entry.Length == 0)
            {
                continue;
            }
            var separator = entry.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }
            var name = entry.Substring(0, separator).Trim();
            var value = entry.Substring(separator + 1).Trim();
            if (name.Length > 0)
            {
                result.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        return result;
    }
}
