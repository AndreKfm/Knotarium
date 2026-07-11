using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using KnotGarden.Core.Contracts;
using KnotGarden.Features.Bundles;
using KnotGarden.Features.Execution;
using KnotGarden.Infrastructure.Persistence;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Host-side DI for the workflow-portability family: folder export, integration bundles (.kgbundle),
/// shareable templates (.kgtpl), and full-instance backup (.kgbak). These registrations wire
/// Infrastructure adapters to Features services, so they belong in the host composition root rather
/// than a Features project (Features must not depend on Infrastructure).
/// </summary>
public static class PortabilityServiceCollectionExtensions
{
    public static IServiceCollection AddPortability(this IServiceCollection services)
    {
        // Shared "current published state" resolver, consumed by the folder exporter, the bundle workflow
        // source, and the template pipeline so they never disagree about which version a workflow exports.
        services.AddScoped<IPublishedWorkflowExportSource, PublishedWorkflowExportSource>();
        services.AddScoped<WorkflowExportService>(serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var exportFolder = configuration["Export:Folder"];
            if (string.IsNullOrWhiteSpace(exportFolder))
            {
                exportFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "KnotGarden",
                    "export");
            }

            return new WorkflowExportService(
                exportFolder,
                serviceProvider.GetRequiredService<IPublishedWorkflowExportSource>());
        });

        // Bundle export pipeline — registry-backed resolution + workflow sources feeding the orchestrator.
        services.AddScoped<IBundlePackageSource, RegistryBundlePackageSource>();
        services.AddScoped<IBundleWorkflowSource, RegistryBundleWorkflowSource>();
        services.AddScoped(serviceProvider => new BundleExportService(
            serviceProvider.GetRequiredService<IBundlePackageSource>(),
            serviceProvider.GetRequiredService<IBundleWorkflowSource>(),
            TimeProvider.System));
        services.AddScoped<IBundleWorkflowImporter, WorkflowPublisherBundleImporter>();
        services.AddScoped<BundleInstallService>();

        // Shareable workflow templates (.kgtpl): export/inspect/install + a built-in starter gallery. A separate,
        // simpler feature than bundles (single workflow, no packages/lock/signatures) sharing the portability core.
        services.AddScoped(serviceProvider => new KnotGarden.Features.Templates.TemplateExportService(
            serviceProvider.GetRequiredService<IPublishedWorkflowExportSource>(),
            serviceProvider.GetRequiredService<AppDbContext>(),
            TimeProvider.System));
        services.AddScoped<KnotGarden.Features.Templates.TemplateCompatibilityChecker>();
        services.AddScoped<KnotGarden.Features.Templates.TemplateInspectService>();
        services.AddScoped<KnotGarden.Features.Templates.TemplatePayloadService>();
        services.AddScoped<KnotGarden.Features.Templates.ITemplateWorkflowImporter,
            KnotGarden.Features.Templates.WorkflowPublisherTemplateImporter>();
        services.AddScoped<KnotGarden.Features.Templates.TemplateInstallService>();
        services.AddScoped(serviceProvider => new KnotGarden.Features.Templates.UserTemplateLibrary(
            serviceProvider.GetRequiredService<AppDbContext>(),
            serviceProvider.GetRequiredService<KnotGarden.Features.Templates.TemplateExportService>(),
            TimeProvider.System));
        services.AddSingleton(_ => new KnotGarden.Features.Templates.BuiltInTemplateGallery(
            KnotGarden.Features.Templates.BuiltInTemplateGallery.DefaultSourcesDirectory));

        // Full-instance backup pipeline (passphrase-encrypted .kgbak snapshots). Distinct from the
        // secret-free .kgbundle distribution path: a backup carries secrets (decrypted, for re-encryption
        // at restore) and replaces state wholesale.
        services.AddScoped<KnotGarden.Features.Backup.BackupService>();

        return services;
    }
}
