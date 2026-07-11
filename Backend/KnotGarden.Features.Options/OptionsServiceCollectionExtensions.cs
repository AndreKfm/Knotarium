using KnotGarden.Core.Contracts.Options;
using KnotGarden.Features.Options;

// Placed in the Microsoft.Extensions.DependencyInjection namespace (the .NET convention for
// DI registration extensions) so callers get AddOptionsFeature() without an extra using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the dynamic-options / resource-locator machinery: the design-time loader registry
/// and its built-in REST-collection loader, the short-TTL options cache, and the execution-time
/// resource resolver. Owns this slice's composition — the host just calls AddOptionsFeature().
/// </summary>
public static class OptionsServiceCollectionExtensions
{
    public static IServiceCollection AddOptionsFeature(this IServiceCollection services)
    {
        // Each registered loader's Name is the design-time allowlist key; the registry rejects
        // anything not registered here. Plugin-provided loaders are registered separately by the host.
        services.AddScoped<IOptionsLoader, RestCollectionOptionsLoader>();
        services.AddScoped<IOptionsLoaderRegistry, OptionsLoaderRegistry>();
        services.AddScoped<ResourceResolver>();

        services.AddMemoryCache();               // OptionsCache backing store (idempotent).
        services.AddSingleton<OptionsCache>();
        return services;
    }
}
