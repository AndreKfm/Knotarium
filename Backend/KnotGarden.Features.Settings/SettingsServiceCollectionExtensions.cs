using KnotGarden.Core.Contracts;
using KnotGarden.Features.Settings;

// .NET convention: DI registration extensions live in Microsoft.Extensions.DependencyInjection
// so callers get AddSettings() without an extra using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the settings slice: the global-settings service that reads/writes instance-wide
/// AppSettings (default notification channels, error-workflow selection, etc.).
/// </summary>
public static class SettingsServiceCollectionExtensions
{
    public static IServiceCollection AddSettings(this IServiceCollection services)
    {
        services.AddScoped<GlobalSettingsService>();

        // File-access policy: one scoped instance serves both the settings API (concrete store) and the
        // file-node guard (via the Core provider seam), overriding the deny-all fallback from AddBuiltInNodes.
        services.AddScoped<FileAccessPolicyStore>();
        services.AddScoped<IFileAccessPolicyProvider>(sp => sp.GetRequiredService<FileAccessPolicyStore>());

        // Capability policy: same shape — concrete store for the API, ICapabilityPolicy seam for the nodes.
        services.AddScoped<CapabilityPolicyStore>();
        services.AddScoped<ICapabilityPolicy>(sp => sp.GetRequiredService<CapabilityPolicyStore>());
        return services;
    }
}
