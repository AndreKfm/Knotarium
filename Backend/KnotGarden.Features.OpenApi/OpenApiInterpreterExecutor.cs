using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Contracts.OpenApi;
using KnotGarden.Core.Domain;
using KnotGarden.Core.Domain.OpenApi;

namespace KnotGarden.Features.OpenApi;

/// <summary>
/// One pre-compiled executor that runs every <c>openapi.*</c> node by interpreting the
/// stored <see cref="ParsedSpec"/> — no per-spec Roslyn compilation. The spec to run is
/// resolved at runtime from the reserved <c>__specId</c> input the dispatcher injects
/// (see <see cref="KnotGarden.Features.Nodes.DynamicCustomNodeTask"/>).
///
/// This is the body of the formerly generated executor template, promoted to a real class:
/// the only change from that template is that <c>SpecId</c> is no longer a baked constant.
/// </summary>
public sealed class OpenApiInterpreterExecutor : INodeExecutor
{
    /// <summary>Reserved input the dispatcher injects to tell the interpreter which spec to run.</summary>
    public const string SpecIdInputKey = "__specId";

    private readonly IOpenApiSpecStore _specStore;
    private readonly IServerConfigStore _serverConfigStore;
    private readonly IOAuthTokenCache? _oAuthTokenCache;
    private readonly IHttpClientFactory? _httpClientFactory;

    public OpenApiInterpreterExecutor(
        IOpenApiSpecStore specStore,
        IServerConfigStore serverConfigStore,
        IOAuthTokenCache? oAuthTokenCache = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        _specStore = specStore;
        _serverConfigStore = serverConfigStore;
        _oAuthTokenCache = oAuthTokenCache;
        _httpClientFactory = httpClientFactory;
    }

    public async ValueTask<NodeResult> ExecuteAsync(
        NodeInput input,
        INodeContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var specId = GetString(input, SpecIdInputKey);
            if (string.IsNullOrEmpty(specId))
                return Fail("specId not provided to interpreter.");

            var operationId = GetString(input, "operationId");
            if (string.IsNullOrEmpty(operationId))
                return Fail("operationId is required.");

            var serverConfigId = GetString(input, "serverConfigId");
            if (string.IsNullOrEmpty(serverConfigId))
                return Fail("serverConfigId is required.");

            // Load spec
            ParsedSpec? parsedSpec;
            var specVersionStr = GetString(input, "specVersion");
            if (!string.IsNullOrEmpty(specVersionStr) && int.TryParse(specVersionStr, out var specVersion))
            {
                var versioned = await _specStore.GetVersionAsync(new OpenApiSpecId(specId), specVersion, cancellationToken);
                parsedSpec = versioned?.Full;
            }
            else
            {
                var latest = await _specStore.GetLatestAsync(new OpenApiSpecId(specId), cancellationToken);
                parsedSpec = latest?.Full;
            }

            if (parsedSpec is null)
                return Fail($"Spec '{specId}' not found.");

            ApiOperation? operation = null;
            foreach (var op in parsedSpec.Operations)
            {
                if (op.OperationId == operationId) { operation = op; break; }
            }
            if (operation is null)
                return Fail($"Operation '{operationId}' not found in spec '{specId}'.");

            // Load server config
            var serverConfig = await _serverConfigStore.GetAsync(serverConfigId, cancellationToken);
            if (serverConfig is null)
                return Fail($"ServerConfig '{serverConfigId}' not found.");

            // A ServerConfig may opt into skipping TLS validation (self-signed / untrusted cert).
            // When it does and a factory is available, route this node's calls through the insecure
            // client (egress policy still applies); otherwise use the node context's HTTP client.
            var insecureClient = (serverConfig.AllowInsecureCertificate && _httpClientFactory != null)
                ? _httpClientFactory.CreateClient("InsecureHttp")
                : null;
            Task<HttpResponseMessage> SendAsync(HttpRequestMessage req) =>
                insecureClient != null
                    ? insecureClient.SendAsync(req, cancellationToken)
                    : context.Http!.SendAsync(req, cancellationToken);
            bool HasHttp() => insecureClient != null || context.Http != null;

            // Parse arguments
            var pathArgs   = new Dictionary<string, string>();
            var queryArgs  = new Dictionary<string, string>();
            var headerArgs = new Dictionary<string, string>();
            var bodyArgs   = new Dictionary<string, string>();
            var argsJson = GetString(input, "arguments") ?? "{}";
            try
            {
                var root = JsonDocument.Parse(argsJson).RootElement;
                FillDict(root, "path",   pathArgs);
                FillDict(root, "query",  queryArgs);
                FillDict(root, "header", headerArgs);
                FillDict(root, "body",   bodyArgs);
            }
            catch { /* use empty args on parse failure */ }

            // Build URL
            var baseUrl = serverConfig.BaseUrl.TrimEnd('/');
            foreach (var kv in serverConfig.ServerVariables)
                baseUrl = baseUrl.Replace("{" + kv.Key + "}", kv.Value);

            var path = operation.PathTemplate;
            foreach (var kv in pathArgs)
                path = path.Replace("{" + kv.Key + "}", Uri.EscapeDataString(kv.Value));

            var url = baseUrl + path;
            if (queryArgs.Count > 0)
            {
                var sep = url.Contains('?') ? "&" : "?";
                url = url + sep + BuildQueryString(queryArgs);
            }

            var method  = new HttpMethod(operation.Method);
            var request = new HttpRequestMessage(method, url);

            foreach (var kv in headerArgs)
                request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            if (bodyArgs.Count > 0)
            {
                var bodyJson  = JsonSerializer.Serialize(bodyArgs);
                var mediaType = (operation.RequestBody != null && operation.RequestBody.MediaTypes.Count > 0)
                    ? operation.RequestBody.MediaTypes[0] : "application/json";
                request.Content = new StringContent(bodyJson, Encoding.UTF8, mediaType);
            }

            // Auth
            if (serverConfig.CredentialRef != null && context.Credentials != null)
            {
                var secret = await context.Credentials.GetSecretAsync(serverConfig.CredentialRef, cancellationToken) ?? "";
                if (serverConfig.SecuritySchemeType == "http_bearer")
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
                }
                else if (serverConfig.SecuritySchemeType == "http_basic")
                {
                    var colonIdx = secret.IndexOf(':');
                    var user    = colonIdx >= 0 ? secret.Substring(0, colonIdx) : secret;
                    var pass    = colonIdx >= 0 ? secret.Substring(colonIdx + 1) : "";
                    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + pass));
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", encoded);
                }
                else if (serverConfig.SecuritySchemeType == "apiKey")
                {
                    SecurityScheme? scheme = null;
                    foreach (var s in parsedSpec.SecuritySchemes)
                    {
                        if (string.Equals(s.Type, "apiKey", StringComparison.OrdinalIgnoreCase))
                        { scheme = s; break; }
                    }
                    var paramName = scheme?.ParamName ?? "X-API-Key";
                    var inVal     = scheme?.In        ?? "header";
                    if (string.Equals(inVal, "header", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.TryAddWithoutValidation(paramName, secret);
                    }
                    else if (string.Equals(inVal, "query", StringComparison.OrdinalIgnoreCase))
                    {
                        var sep2 = url.Contains('?') ? "&" : "?";
                        url     = url + sep2 + Uri.EscapeDataString(paramName) + "=" + Uri.EscapeDataString(secret);
                        var rebuilt = new HttpRequestMessage(method, url);
                        foreach (var kv in headerArgs)
                            rebuilt.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                        if (request.Content != null) rebuilt.Content = request.Content;
                        request = rebuilt;
                    }
                }
                else if (serverConfig.SecuritySchemeType == "oauth2")
                {
                    if (_oAuthTokenCache is null)
                        return Fail("OAuth2 requires IOAuthTokenCache — not registered.");

                    SecurityScheme? oauth2Scheme = null;
                    foreach (var s in parsedSpec.SecuritySchemes)
                    {
                        if (string.Equals(s.Type, "oauth2", StringComparison.OrdinalIgnoreCase))
                        { oauth2Scheme = s; break; }
                    }
                    var tokenUrl = oauth2Scheme?.TokenUrl ?? string.Empty;
                    if (string.IsNullOrEmpty(tokenUrl))
                        return Fail("OAuth2 tokenUrl not found in security scheme.");

                    var colonIdx2    = secret.IndexOf(':');
                    var clientId     = colonIdx2 >= 0 ? secret.Substring(0, colonIdx2) : secret;
                    var clientSecret = colonIdx2 >= 0 ? secret.Substring(colonIdx2 + 1) : string.Empty;
                    var cacheKey     = serverConfigId + ":" + (serverConfig.CredentialRef ?? string.Empty);

                    if (!HasHttp())
                        return Fail("No HTTP client available.");

                    var token1 = await _oAuthTokenCache.GetTokenAsync(
                        cacheKey, tokenUrl, clientId, clientSecret, Array.Empty<string>(), cancellationToken);
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token1);

                    using var resp1  = await SendAsync(request);
                    if ((int)resp1.StatusCode == 401)
                    {
                        _oAuthTokenCache.Invalidate(cacheKey);
                        var token2 = await _oAuthTokenCache.GetTokenAsync(
                            cacheKey, tokenUrl, clientId, clientSecret, Array.Empty<string>(), cancellationToken);

                        using var retryReq = new HttpRequestMessage(method, url);
                        foreach (var kv in headerArgs)
                            retryReq.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                        retryReq.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token2);
                        if (bodyArgs.Count > 0)
                        {
                            var bodyJson2  = JsonSerializer.Serialize(bodyArgs);
                            var mediaType2 = (operation.RequestBody != null && operation.RequestBody.MediaTypes.Count > 0)
                                ? operation.RequestBody.MediaTypes[0] : "application/json";
                            retryReq.Content = new StringContent(bodyJson2, Encoding.UTF8, mediaType2);
                        }

                        using var resp2  = await SendAsync(retryReq);
                        var body2  = await resp2.Content.ReadAsStringAsync(cancellationToken);
                        var sc2    = (int)resp2.StatusCode;
                        var pay2   = JsonSerializer.SerializeToElement(new { statusCode = sc2, body = body2 });
                        return new NodeResult(
                            resp2.IsSuccessStatusCode ? "success" : "error", pay2,
                            NodeExecutionStatus.Succeeded);
                    }

                    var body1  = await resp1.Content.ReadAsStringAsync(cancellationToken);
                    var sc1    = (int)resp1.StatusCode;
                    var pay1   = JsonSerializer.SerializeToElement(new { statusCode = sc1, body = body1 });
                    return new NodeResult(
                        resp1.IsSuccessStatusCode ? "success" : "error", pay1,
                        NodeExecutionStatus.Succeeded);
                }
            }

            if (!HasHttp())
                return Fail("No HTTP client available.");

            using var response     = await SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode   = (int)response.StatusCode;
            var payload      = JsonSerializer.SerializeToElement(new { statusCode, body = responseBody });

            // Always Succeeded at the node level — the HTTP outcome is expressed via the output port.
            // Returning Failed here would cause DynamicCustomNodeTask to suppress the payload and
            // show a generic "Node execution failed." instead of the actual status code + body.
            return new NodeResult(
                response.IsSuccessStatusCode ? "success" : "error",
                payload,
                NodeExecutionStatus.Succeeded);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static void FillDict(JsonElement root, string key, Dictionary<string, string> target)
    {
        if (!root.TryGetProperty(key, out var section) || section.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in section.EnumerateObject())
            target[prop.Name] = ExtractArgValue(prop.Value);
    }

    /// <summary>
    /// Reads an argument value into the string the URL/body needs. A resource-locator selection is
    /// persisted as <c>{ value, label, mode }</c> (or a multi <c>{ mode, items: [...] }</c>); for
    /// those we use the stored stable key(s), ignoring the display-only label. Everything else is
    /// taken verbatim (plain string) or as raw JSON (objects/arrays).
    /// </summary>
    private static string ExtractArgValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!;

        if (IsStoredResourceLocator(value))
        {
            var keys = KnotGarden.Features.Options.StoredOptionValue.ReadValues(value);
            // Path params are single; a multi-select query value joins with commas (array form).
            return keys.Count switch
            {
                0 => string.Empty,
                1 => keys[0],
                _ => string.Join(",", keys),
            };
        }

        return value.GetRawText();
    }

    private static bool IsStoredResourceLocator(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("mode", out _)
        && (value.TryGetProperty("value", out _) || value.TryGetProperty("items", out _));

    private static string BuildQueryString(Dictionary<string, string> dict)
    {
        var parts = new List<string>();
        foreach (var kv in dict)
            parts.Add(Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value));
        return string.Join("&", parts);
    }

    private static NodeResult Fail(string message) =>
        new NodeResult("error",
            JsonSerializer.SerializeToElement(new { error = message }),
            NodeExecutionStatus.Failed);

    private static string? GetString(NodeInput input, string name)
    {
        if (!input.Parameters.TryGetValue(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
    }
}
