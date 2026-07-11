using KnotGarden.Core.Contracts.OpenApi;
using KnotGarden.Features.OpenApi;

// .NET convention: DI registration extensions live in Microsoft.Extensions.DependencyInjection
// so callers get AddOpenApiFeature() without an extra using. Named *Feature to avoid colliding with
// the framework's Microsoft.AspNetCore.OpenApi AddOpenApi() (same namespace) — mirrors AddOptionsFeature().
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the OpenAPI slice: the node generator that turns a parsed spec into node manifests, the
/// import/delete spec handlers, and the two Core inversion seams other slices consume — the
/// interpreter-executor factory (Nodes) and the request-auth applier (Polling). The parser,
/// spec/server-config stores and OAuth token cache are infrastructure/persistence concerns and stay
/// wired in the host.
/// </summary>
public static class OpenApiServiceCollectionExtensions
{
    public static IServiceCollection AddOpenApiFeature(this IServiceCollection services)
    {
        services.AddSingleton<OpenApiNodeGenerator>();
        services.AddScoped<ImportOpenApiSpecHandler>();
        services.AddScoped<DeleteOpenApiSpecHandler>();
        services.AddScoped<IOpenApiInterpreterExecutorFactory, OpenApiInterpreterExecutorFactory>();
        services.AddSingleton<IOpenApiRequestAuthApplier, OpenApiRequestAuthApplier>();
        return services;
    }
}
