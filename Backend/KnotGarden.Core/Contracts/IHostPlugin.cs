using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using KnotGarden.Core.Contracts.Options;

namespace KnotGarden.Core.Contracts;

/// <summary>
/// Entry point for a binary host-service plugin: a self-contained DLL dropped into the host's
/// <c>plugins/</c> folder that contributes long-lived, always-on capabilities (an
/// <see cref="IExternalSignalProvider"/>, option-loaders for editor pickers, background loops).
/// Discovered and instantiated by the host at startup. The contract is deliberately DI-free so the
/// vendored core assembly carries no framework coupling — plugins register everything through the
/// host-supplied <see cref="IHostPluginBuilder"/>.
/// </summary>
public interface IHostPlugin
{
    /// <summary>Stable, human-readable plugin name (for logs/diagnostics).</summary>
    string Name { get; }

    /// <summary>
    /// Called once at host startup. Register the plugin's provider, option-loaders and background
    /// loops on the builder. Do not block — connection/lifecycle work belongs in a background loop
    /// or lazily behind <see cref="IExternalSignalProvider.Acquire"/>.
    /// </summary>
    void Configure(IHostPluginBuilder builder);
}

/// <summary>
/// Host-implemented registration surface handed to an <see cref="IHostPlugin"/>. Exposes only what a
/// plugin needs — no service-collection or configuration types cross the seam.
/// </summary>
public interface IHostPluginBuilder
{
    /// <summary>Logger factory for the plugin to create its own loggers.</summary>
    ILoggerFactory LoggerFactory { get; }

    /// <summary>Read a host configuration value (e.g. plugin settings) by key, or null.</summary>
    string? GetSetting(string key);

    /// <summary>
    /// Register the plugin's external-signal provider. At most one provider may be registered across
    /// all plugins; a second registration is an error surfaced by the host.
    /// </summary>
    void AddExternalSignalProvider(IExternalSignalProvider provider);

    /// <summary>
    /// Register the plugin's optional administration surface (UI-driven create/edit of targets). At most
    /// one may be registered across all plugins. Providers that are config-file-only simply skip this.
    /// </summary>
    void AddExternalSignalAdmin(IExternalSignalAdmin admin);

    /// <summary>Register an editor option-loader (cascaded resource-locator pickers).</summary>
    void AddOptionsLoader(IOptionsLoader loader);

    /// <summary>
    /// Register an import provider that turns an uploaded vendor file into generic workflows. Multiple
    /// providers may be registered (one per source format); the host lists them and routes uploads by id.
    /// </summary>
    void AddWorkflowImportProvider(IWorkflowImportProvider provider);

    /// <summary>
    /// Register a long-lived background loop started with the host and cancelled on shutdown
    /// (e.g. an inbound-bus drain). The host owns the task lifecycle.
    /// </summary>
    void AddBackgroundLoop(string name, Func<CancellationToken, Task> run);
}
