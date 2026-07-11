using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.OpenApi;
using Knotarium.Core.Domain.OpenApi;

namespace Knotarium.Features.Polling;

/// <summary>
/// Adapts the OpenAPI spec store and server config to the polling invoker seam.
/// Makes a minimal GET-style HTTP call (no path/query/body arguments) against the resolved
/// operation endpoint so the poll source can inspect the response body and headers.
/// This is the ONLY file in the polling stack that is coupled to the OpenAPI infrastructure.
///
/// Authentication is applied via the <see cref="IOpenApiRequestAuthApplier"/> Core seam (implemented
/// in the OpenApi slice), which mirrors the inline auth logic in the OpenAPI interpreter. Supported
/// schemes: http_bearer, http_basic, apiKey
/// (header and query). OAuth2 is not supported in the polling adapter (a clear
/// <see cref="NotSupportedException"/> is thrown so the poll records a LastError rather than
/// silently sending unauthenticated requests).
/// </summary>
public sealed class OpenApiOperationInvoker : IOpenApiOperationInvoker
{
    private readonly IOpenApiSpecStore _specStore;
    private readonly IServerConfigStore _serverConfigStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretResolver _secretResolver;
    private readonly IOpenApiRequestAuthApplier _authApplier;

    public OpenApiOperationInvoker(
        IOpenApiSpecStore specStore,
        IServerConfigStore serverConfigStore,
        IHttpClientFactory httpClientFactory,
        ISecretResolver secretResolver,
        IOpenApiRequestAuthApplier authApplier)
    {
        _specStore = specStore ?? throw new ArgumentNullException(nameof(specStore));
        _serverConfigStore = serverConfigStore ?? throw new ArgumentNullException(nameof(serverConfigStore));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _secretResolver = secretResolver ?? throw new ArgumentNullException(nameof(secretResolver));
        _authApplier = authApplier ?? throw new ArgumentNullException(nameof(authApplier));
    }

    public async Task<OpenApiPollResponse> InvokeAsync(
        string serverConfigId,
        string operationId,
        string? specVersion,
        CancellationToken cancellationToken)
    {
        // Load server config first so we can find the associated spec id
        var serverConfig = await _serverConfigStore.GetAsync(serverConfigId, cancellationToken)
            ?? throw new InvalidOperationException($"ServerConfig '{serverConfigId}' not found.");

        // Find the operation across all specs (we search all specs for the operation ID,
        // since the poll config identifies by serverConfigId + operationId rather than specId).
        // To keep this efficient, we list imported specs and search for the matching operation.
        var specs = await _specStore.ListAsync(cancellationToken);

        ApiOperation? operation = null;
        IReadOnlyList<SecurityScheme> securitySchemes = Array.Empty<SecurityScheme>();

        foreach (var importedSpec in specs)
        {
            ParsedSpec? parsedSpec;
            if (!string.IsNullOrEmpty(specVersion) && int.TryParse(specVersion, out var versionNum))
            {
                var versioned = await _specStore.GetVersionAsync(importedSpec.Id, versionNum, cancellationToken);
                parsedSpec = versioned?.Full;
            }
            else
            {
                var latest = await _specStore.GetLatestAsync(importedSpec.Id, cancellationToken);
                parsedSpec = latest?.Full;
            }

            var found = FindOperation(parsedSpec, operationId);
            if (found is not null)
            {
                operation = found;
                securitySchemes = parsedSpec!.SecuritySchemes;
                break;
            }
        }

        if (operation is null)
            throw new InvalidOperationException($"Operation '{operationId}' not found in any imported spec.");

        // Build URL from server config base + path template (no path/query substitution for poll — GET-style)
        var baseUrl = serverConfig.BaseUrl.TrimEnd('/');
        foreach (var kv in serverConfig.ServerVariables)
            baseUrl = baseUrl.Replace("{" + kv.Key + "}", kv.Value);

        var url = baseUrl + operation.PathTemplate;
        var method = new HttpMethod(operation.Method);

        // Use the InsecureHttp named client if server config opts in, otherwise the default HttpNode client
        var clientName = serverConfig.AllowInsecureCertificate ? "InsecureHttp" : "HttpNode";
        var client = _httpClientFactory.CreateClient(clientName);
        var request = new HttpRequestMessage(method, url);

        // Accept JSON by preference so the response body is parseable
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Apply authentication from the server config via OpenApiRequestAuthApplier
        // (mirrors OpenApiInterpreterExecutor's inline non-oauth2 auth logic).
        // OAuth2 throws NotSupportedException — PollEvaluationService wraps each poll in try/catch
        // and will record the exception as LastError without enqueuing a false-positive run.
        // The apiKey-in-query scheme rebuilds the request, so dispose the original if replaced.
        var originalRequest = request;
        (request, _) = await _authApplier.ApplyAsync(
            request, serverConfig, securitySchemes, _secretResolver, cancellationToken);
        if (!ReferenceEquals(originalRequest, request))
        {
            originalRequest.Dispose();
        }

        using var requestToSend = request;
        using var response = await client.SendAsync(requestToSend, cancellationToken);

        // Client/server error status codes (>= 400) must NOT be treated as poll data — that would
        // cause auth errors to look like new payload on every poll. Throw so PollEvaluationService
        // records a LastError and advances the timer without enqueueing. (3xx, incl. 304, is left to
        // the client and never treated as an error here.)
        if ((int)response.StatusCode >= 400)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"OpenAPI poll request failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {Truncate(errorBody, 200)}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        // Extract ETag from response headers; EntityTag.Tag includes the surrounding quotes.
        var etag = response.Headers.ETag?.Tag;

        // Extract Last-Modified in RFC-1123 format so it can be sent back as If-Modified-Since.
        var lastModified = response.Content.Headers.LastModified
            ?.ToString("R", CultureInfo.InvariantCulture);

        return new OpenApiPollResponse(body, etag, lastModified);
    }

    private static ApiOperation? FindOperation(ParsedSpec? parsedSpec, string operationId)
    {
        if (parsedSpec is null) return null;
        foreach (var op in parsedSpec.Operations)
        {
            if (op.OperationId == operationId) return op;
        }
        return null;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value.Substring(0, maxLength) + "…";
}
