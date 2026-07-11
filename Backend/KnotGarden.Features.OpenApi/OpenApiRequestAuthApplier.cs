using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Contracts.OpenApi;
using KnotGarden.Core.Domain.OpenApi;

namespace KnotGarden.Features.OpenApi;

/// <summary>
/// Applies the authentication credentials from a <see cref="ServerConfigInfo"/> to an outgoing
/// <see cref="HttpRequestMessage"/>, for the schemes that can be applied without sending the
/// request first (http_bearer, http_basic, apiKey header, apiKey query).
///
/// <para>Currently consumed by <c>KnotGarden.Features.Polling.OpenApiOperationInvoker</c>. It mirrors the
/// inline non-oauth2 auth logic in <see cref="OpenApiInterpreterExecutor"/>; consolidating the
/// interpreter onto this helper is a tracked follow-up (until then, keep the two in sync).</para>
///
/// <para>OAuth2 is NOT handled here — it requires sending the request inside the auth block
/// (to detect 401 + retry) and is therefore kept inline in the interpreter. The polling adapter
/// throws <see cref="NotSupportedException"/> for oauth2 so failures are recorded as LastError
/// rather than silently sending unauthenticated requests.</para>
/// </summary>
public sealed class OpenApiRequestAuthApplier : IOpenApiRequestAuthApplier
{
    /// <summary>
    /// Applies auth to <paramref name="request"/> and returns the (possibly rebuilt) request.
    /// The apiKey-in-query scheme rebuilds the <see cref="HttpRequestMessage"/> with an updated URL;
    /// all other schemes mutate <paramref name="request"/> in place and return it.
    /// </summary>
    /// <param name="request">The outgoing request (may be rebuilt for query-param apiKey).</param>
    /// <param name="serverConfig">Server configuration carrying the scheme type and credential reference.</param>
    /// <param name="securitySchemes">Security schemes from the parsed spec (needed for apiKey param name / location).</param>
    /// <param name="secretResolver">Resolves the credential reference to the raw secret string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The (potentially new) <see cref="HttpRequestMessage"/> with auth applied, and a bool
    /// indicating whether the scheme was recognised and applied. Returns <c>false</c> for
    /// unknown scheme types (no-op, request unchanged).
    /// </returns>
    /// <exception cref="NotSupportedException">Thrown when <paramref name="serverConfig"/> uses
    /// <c>oauth2</c> — callers that cannot handle the request/retry dance should catch this and
    /// record an error instead of proceeding unauthenticated.</exception>
    public async Task<(HttpRequestMessage Request, bool Applied)> ApplyAsync(
        HttpRequestMessage request,
        ServerConfigInfo serverConfig,
        IReadOnlyList<SecurityScheme> securitySchemes,
        ISecretResolver secretResolver,
        CancellationToken cancellationToken)
    {
        if (serverConfig.CredentialRef is null)
            return (request, false);

        if (serverConfig.SecuritySchemeType == "oauth2")
            throw new NotSupportedException(
                "OAuth2 authentication is not supported in the polling adapter. " +
                "Use http_bearer, http_basic, or apiKey instead.");

        var secret = await secretResolver.ResolveAsync(serverConfig.CredentialRef, cancellationToken) ?? string.Empty;

        if (serverConfig.SecuritySchemeType == "http_bearer")
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
            return (request, true);
        }

        if (serverConfig.SecuritySchemeType == "http_basic")
        {
            var colonIdx = secret.IndexOf(':');
            var user     = colonIdx >= 0 ? secret.Substring(0, colonIdx) : secret;
            var pass     = colonIdx >= 0 ? secret.Substring(colonIdx + 1) : string.Empty;
            var encoded  = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + pass));
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", encoded);
            return (request, true);
        }

        if (serverConfig.SecuritySchemeType == "apiKey")
        {
            SecurityScheme? scheme = null;
            foreach (var s in securitySchemes)
            {
                if (string.Equals(s.Type, "apiKey", StringComparison.OrdinalIgnoreCase))
                { scheme = s; break; }
            }

            var paramName = scheme?.ParamName ?? "X-API-Key";
            var inVal     = scheme?.In        ?? "header";

            if (string.Equals(inVal, "header", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(paramName, secret);
                return (request, true);
            }

            if (string.Equals(inVal, "query", StringComparison.OrdinalIgnoreCase))
            {
                var originalUrl = request.RequestUri?.ToString() ?? string.Empty;
                var sep         = originalUrl.Contains('?') ? "&" : "?";
                var newUrl      = originalUrl + sep + Uri.EscapeDataString(paramName) + "=" + Uri.EscapeDataString(secret);

                var rebuilt = new HttpRequestMessage(request.Method, newUrl);
                // Copy headers from the original request
                foreach (var header in request.Headers)
                    rebuilt.Headers.TryAddWithoutValidation(header.Key, header.Value);
                // Copy content if present
                if (request.Content != null)
                    rebuilt.Content = request.Content;

                return (rebuilt, true);
            }
        }

        // Unknown / no-op scheme type
        return (request, false);
    }
}
