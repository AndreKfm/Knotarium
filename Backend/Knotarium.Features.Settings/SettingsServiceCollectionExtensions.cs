// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Contracts;
using Knotarium.Features.Settings;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // Data-retention policy: read by the settings API and re-read live by the JournalRetentionWorker
        // on each sweep. No Core seam — the worker resolves this concrete store from its per-sweep scope.
        // TryAdd a built-in defaults fallback so the slice works standalone; the host overrides it with a
        // config-bound RetentionDefaults (last AddSingleton wins) so appsettings "Retention:*" seeds the UI.
        services.TryAddSingleton(new RetentionDefaults());
        services.AddScoped<RetentionPolicyStore>();

        // Disk-space guard policy: same shape as retention — read by the settings API and re-read live by
        // the DiskSpaceGuardWorker on each check. The host overrides the built-in defaults from "Storage".
        services.TryAddSingleton(new DiskSpaceDefaults());
        services.AddScoped<DiskSpacePolicyStore>();
        return services;
    }
}
