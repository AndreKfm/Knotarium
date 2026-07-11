using System.Net.Http;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Contracts.OpenApi;

namespace KnotGarden.Features.OpenApi;

/// <summary>
/// Host-wired implementation of <see cref="IOpenApiInterpreterExecutorFactory"/>. Holds the stores
/// the interpreter needs and hands the Nodes slice a fresh <see cref="OpenApiInterpreterExecutor"/>
/// without exposing this slice's concrete types across the boundary.
/// </summary>
public sealed class OpenApiInterpreterExecutorFactory(
    IOpenApiSpecStore specStore,
    IServerConfigStore serverConfigStore,
    IOAuthTokenCache? oAuthTokenCache = null,
    IHttpClientFactory? httpClientFactory = null)
    : IOpenApiInterpreterExecutorFactory
{
    public string SpecIdInputKey => OpenApiInterpreterExecutor.SpecIdInputKey;

    public INodeExecutor Create() =>
        new OpenApiInterpreterExecutor(specStore, serverConfigStore, oAuthTokenCache, httpClientFactory);
}
