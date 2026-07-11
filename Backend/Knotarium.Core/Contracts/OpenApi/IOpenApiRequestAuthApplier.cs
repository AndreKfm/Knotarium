using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain.OpenApi;

namespace Knotarium.Core.Contracts.OpenApi;

/// <summary>
/// Applies the authentication credentials from a <see cref="ServerConfigInfo"/> to an outgoing
/// <see cref="HttpRequestMessage"/>, for the schemes that can be applied without sending the request
/// first (http_bearer, http_basic, apiKey header/query). Inversion seam so the Polling slice can
/// authenticate OpenAPI requests without referencing the OpenApi feature slice; the implementation
/// lives in the OpenApi slice and is wired by the host.
/// </summary>
public interface IOpenApiRequestAuthApplier
{
    /// <summary>
    /// Applies auth to <paramref name="request"/> and returns the (possibly rebuilt) request plus a
    /// flag indicating whether a scheme was recognised and applied. The apiKey-in-query scheme
    /// rebuilds the request with an updated URL; all other schemes mutate it in place.
    /// </summary>
    /// <exception cref="System.NotSupportedException">Thrown when the server config uses <c>oauth2</c>,
    /// which requires the request/retry dance and cannot be applied ahead of time.</exception>
    Task<(HttpRequestMessage Request, bool Applied)> ApplyAsync(
        HttpRequestMessage request,
        ServerConfigInfo serverConfig,
        IReadOnlyList<SecurityScheme> securitySchemes,
        ISecretResolver secretResolver,
        CancellationToken cancellationToken);
}
