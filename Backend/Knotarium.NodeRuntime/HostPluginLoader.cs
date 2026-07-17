// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.Options;

namespace Knotarium.NodeRuntime;

/// <summary>
/// What a single loaded plugin contributed.
/// </summary>
public sealed record HostPluginBackgroundLoop(string Name, Func<CancellationToken, Task> Run);

/// <summary>
/// Aggregate of everything contributed by all loaded plugins. Registered as a singleton; Program.cs
/// projects <see cref="SignalProvider"/> and <see cref="OptionsLoaders"/> into DI, and
/// <see cref="HostPluginBackgroundRunner"/> drives <see cref="BackgroundLoops"/>.
/// </summary>
public sealed class HostPluginRegistry
{
    public IExternalSignalProvider? SignalProvider { get; init; }
    public IExternalSignalAdmin? SignalAdmin { get; init; }
    public IReadOnlyList<IOptionsLoader> OptionsLoaders { get; init; } = Array.Empty<IOptionsLoader>();
    public IReadOnlyList<IWorkflowImportProvider> ImportProviders { get; init; } = Array.Empty<IWorkflowImportProvider>();
    public IReadOnlyList<HostPluginBackgroundLoop> BackgroundLoops { get; init; } = Array.Empty<HostPluginBackgroundLoop>();
    public IReadOnlyList<string> LoadedPlugins { get; init; } = Array.Empty<string>();
}

/// <summary>Host-side implementation of the plugin registration surface.</summary>
internal sealed class HostPluginBuilder : IHostPluginBuilder
{
    private readonly IConfiguration _configuration;
    public ILoggerFactory LoggerFactory { get; }

    public IExternalSignalProvider? Provider { get; private set; }
    public IExternalSignalAdmin? Admin { get; private set; }
    public List<IOptionsLoader> Loaders { get; } = new();
    public List<IWorkflowImportProvider> ImportProviders { get; } = new();
    public List<HostPluginBackgroundLoop> Loops { get; } = new();

    public HostPluginBuilder(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        _configuration = configuration;
        LoggerFactory = loggerFactory;
    }

    public string? GetSetting(string key) => _configuration[key];

    public void AddExternalSignalProvider(IExternalSignalProvider provider)
    {
        if (Provider != null)
        {
            throw new InvalidOperationException(
                "An external-signal provider has already been registered by another plugin; only one is supported.");
        }
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public void AddExternalSignalAdmin(IExternalSignalAdmin admin)
    {
        if (Admin != null)
        {
            throw new InvalidOperationException(
                "An external-signal admin has already been registered by another plugin; only one is supported.");
        }
        Admin = admin ?? throw new ArgumentNullException(nameof(admin));
    }

    public void AddOptionsLoader(IOptionsLoader loader)
        => Loaders.Add(loader ?? throw new ArgumentNullException(nameof(loader)));

    public void AddWorkflowImportProvider(IWorkflowImportProvider provider)
        => ImportProviders.Add(provider ?? throw new ArgumentNullException(nameof(provider)));

    public void AddBackgroundLoop(string name, Func<CancellationToken, Task> run)
        => Loops.Add(new HostPluginBackgroundLoop(name, run ?? throw new ArgumentNullException(nameof(run))));
}

/// <summary>
/// Discovers and configures binary host-service plugins at startup (configuration-time, before the
/// host is built, so their providers/loaders join real DI and their background loops can be hosted).
/// Each top-level <c>*.dll</c> in the plugins folder that declares a manifest is probed for an
/// <see cref="IHostPlugin"/> implementation.
/// </summary>
public static class HostPluginLoader
{
    /// <summary>
    /// Scan <paramref name="pluginsPath"/> for plugin DLLs and run their <see cref="IHostPlugin.Configure"/>.
    /// Failures are logged and isolated — one broken plugin never blocks host startup.
    /// </summary>
    public static HostPluginRegistry Load(string pluginsPath, IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("HostPluginLoader");

        if (!Directory.Exists(pluginsPath))
        {
            try { Directory.CreateDirectory(pluginsPath); } catch { /* best effort */ }
            logger.LogInformation("No plugins folder at '{PluginsPath}' (created); no host plugins loaded.", pluginsPath);
            return new HostPluginRegistry();
        }

        var builder = new HostPluginBuilder(configuration, loggerFactory);
        var loaded = new List<string>();

        // Each plugin lives in its own subfolder (so its private dependencies don't collide):
        //   plugins/<Name>/<Name>.dll (+ SDK deps beside it)
        foreach (var dir in Directory.EnumerateDirectories(pluginsPath))
        {
            // Probe ONLY the entry assembly (the DLL named after the folder), NOT every DLL beside it.
            // The vendor SDK DLLs sitting alongside are frequently mixed-mode native wrappers; eagerly
            // loading one of those just to check for an IHostPlugin can hard-crash the host process when
            // its native dependencies fault. The plugin's own load context pulls those in lazily, only
            // when the plugin actually uses them. Fall back to a scan only if the entry isn't named by
            // convention (then break on the first hit — one plugin per folder).
            var entryDll = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
            var candidates = File.Exists(entryDll)
                ? new[] { entryDll }
                : Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly);

            foreach (var dll in candidates)
            {
                try
                {
                    if (TryConfigurePlugin(dll, builder, logger, out var name))
                    {
                        loaded.Add(name!);
                        break; // one plugin per folder — don't probe the SDK DLLs beside it
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to load host plugin candidate '{Dll}'.", dll);
                }
            }
        }

        if (loaded.Count == 0)
        {
            logger.LogInformation("No host plugins found under '{PluginsPath}'.", pluginsPath);
        }

        return new HostPluginRegistry
        {
            SignalProvider = builder.Provider,
            SignalAdmin = builder.Admin,
            OptionsLoaders = builder.Loaders,
            ImportProviders = builder.ImportProviders,
            BackgroundLoops = builder.Loops,
            LoadedPlugins = loaded,
        };
    }

    private static bool TryConfigurePlugin(string dllPath, HostPluginBuilder builder, ILogger logger, out string? pluginName)
    {
        pluginName = null;
        var alc = new PluginAssemblyLoadContext(dllPath);
        Assembly assembly;
        try
        {
            assembly = alc.LoadFromAssemblyPath(dllPath);
        }
        catch (BadImageFormatException)
        {
            // Native or non-managed DLL in the folder — not a plugin entry assembly.
            return false;
        }

        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException rtl) { types = rtl.Types.Where(t => t != null).ToArray()!; }

        var pluginType = types.FirstOrDefault(t =>
            typeof(IHostPlugin).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false });
        if (pluginType == null)
        {
            return false;
        }

        var plugin = (IHostPlugin)Activator.CreateInstance(pluginType)!;
        plugin.Configure(builder);
        pluginName = plugin.Name;
        logger.LogInformation("Loaded host plugin '{Name}' from '{Dll}'.", plugin.Name, dllPath);
        return true;
    }
}

/// <summary>
/// Drives the background loops contributed by plugins for the lifetime of the host. Each loop runs
/// on its own long-lived task; a faulting loop is logged but does not crash the host.
/// </summary>
public sealed class HostPluginBackgroundRunner : IHostedService
{
    private readonly HostPluginRegistry _registry;
    private readonly ILogger<HostPluginBackgroundRunner> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _tasks = new();

    public HostPluginBackgroundRunner(HostPluginRegistry registry, ILogger<HostPluginBackgroundRunner> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var loop in _registry.BackgroundLoops)
        {
            var captured = loop;
            _tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await captured.Run(_cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    // graceful shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Plugin background loop '{Name}' faulted.", captured.Name);
                }
            }));
            _logger.LogInformation("Started plugin background loop '{Name}'.", captured.Name);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        try
        {
            await Task.WhenAll(_tasks).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) { /* shutdown deadline */ }
        catch (Exception ex) { _logger.LogDebug(ex, "Plugin background loops ended with errors during shutdown."); }
    }
}
