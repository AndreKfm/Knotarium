using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Host-side DI for binary host-service plugins: self-contained DLLs under the plugins/ folder that
/// contribute an in-process <c>IExternalSignalProvider</c>, editor option-loaders and always-on
/// background loops. Loaded at configuration-time so their contributions join real DI and their loops
/// can be hosted. The host stays vendor-agnostic — it never names any specific provider.
/// </summary>
public static class HostPluginServiceCollectionExtensions
{
    public static IServiceCollection AddHostPlugins(this IServiceCollection services, IConfiguration configuration)
    {
        var pluginsPath = configuration["NodeRuntime:PluginsPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "plugins");
        // Long-lived: plugins capture this factory for DEFERRED work (background loops, lazy adapter creation,
        // test-connection). It must NOT be disposed after Load() — the plugins hold it for the host process
        // lifetime, so any later CreateLogger call (e.g. a test-connection request) would otherwise throw
        // ObjectDisposedException. Intentionally never disposed.
        var pluginLoggerFactory = LoggerFactory.Create(lb => lb.AddConsole());

        var hostPlugins = KnotGarden.NodeRuntime.HostPluginLoader.Load(pluginsPath, configuration, pluginLoggerFactory);
        services.AddSingleton(hostPlugins);
        if (hostPlugins.SignalProvider != null)
        {
            services.AddSingleton<KnotGarden.Core.Contracts.IExternalSignalProvider>(hostPlugins.SignalProvider);
        }
        if (hostPlugins.SignalAdmin != null)
        {
            services.AddSingleton<KnotGarden.Core.Contracts.IExternalSignalAdmin>(hostPlugins.SignalAdmin);
        }
        foreach (var optionsLoader in hostPlugins.OptionsLoaders)
        {
            services.AddSingleton<KnotGarden.Core.Contracts.Options.IOptionsLoader>(optionsLoader);
        }
        if (hostPlugins.BackgroundLoops.Count > 0)
        {
            services.AddHostedService<KnotGarden.NodeRuntime.HostPluginBackgroundRunner>();
        }

        return services;
    }
}
