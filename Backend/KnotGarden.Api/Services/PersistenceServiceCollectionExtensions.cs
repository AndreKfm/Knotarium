using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using KnotGarden.Core.Contracts;
using KnotGarden.Features.Execution;
using KnotGarden.Infrastructure.Persistence;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Host-side DI for persistence: the dynamic database-provider factory, the pooled
/// <see cref="AppDbContext"/>, the provider-selected + instrumented execution-journal writer, and the
/// EF-backed Core read/write adapters that seam the feature slices off EF. These registrations wire
/// Infrastructure adapters to Core/Features seams, so they belong in the host composition root rather
/// than a Features project (Features must not depend on Infrastructure).
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Database provider factory + <see cref="AppDbContext"/> + the journal-writer chain (provider-selected
    /// inner writer wrapped in the telemetry-instrumented decorator). <paramref name="appBaseDir"/> anchors the
    /// default SQLite file next to the executable in a productive build; <paramref name="isDevelopment"/> keeps
    /// the plain relative path (project dir) in Development.
    /// </summary>
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        string appBaseDir,
        bool isDevelopment)
    {
        // Register dynamic database providers and factory
        services.AddSingleton<IDatabaseProvider, SqliteDatabaseProvider>();
        services.AddSingleton<IDatabaseProvider, PostgresDatabaseProvider>();
        services.AddSingleton<DatabaseProviderFactory>();

        var dbProviderName = configuration["Database:Provider"] ?? "SQLite";
        var dbConnectionString = configuration["Database:ConnectionString"];
        if (string.IsNullOrWhiteSpace(dbConnectionString))
        {
            // Default SQLite database. In a published (productive) build, anchor the file next to
            // the executable so a copied folder is self-contained no matter the launch working
            // directory. In Development we keep the plain relative path (project dir) as before.
            var dbFile = isDevelopment
                ? "KnotGarden.db"
                : Path.Combine(appBaseDir, "KnotGarden.db");
            dbConnectionString = $"Data Source={dbFile}";
        }

        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            var factory = serviceProvider.GetRequiredService<DatabaseProviderFactory>();
            var provider = factory.GetProvider(dbProviderName);
            provider.Configure(options, dbConnectionString);
        });

        // Register high-speed journal writer
        if (string.Equals(dbProviderName, "SQLite", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<SqliteExecutionJournalWriter>();
        }
        else
        {
            services.AddSingleton<PostgresExecutionJournalWriter>();
        }

        services.AddSingleton<ExecutionTelemetry>();
        // Narrow producer seam so node tasks can emit outbound-HTTP spans without depending on the Execution
        // slice. Aliased to the same singleton; load-bearing because HttpRequestNodeTask's telemetry param is
        // optional (defaults to null) and MS.DI honors that default when the type is unregistered.
        services.AddSingleton<IOutboundHttpTelemetry>(sp => sp.GetRequiredService<ExecutionTelemetry>());
        services.AddSingleton<IExecutionJournalWriter>(sp =>
        {
            var telemetry = sp.GetRequiredService<ExecutionTelemetry>();
            var inner = string.Equals(dbProviderName, "SQLite", StringComparison.OrdinalIgnoreCase)
                ? (IExecutionJournalWriter)sp.GetRequiredService<SqliteExecutionJournalWriter>()
                : sp.GetRequiredService<PostgresExecutionJournalWriter>();

            return new InstrumentedExecutionJournalWriter(inner, telemetry);
        });

        return services;
    }

    /// <summary>
    /// The EF-backed Core adapters: one <see cref="AppDbContext"/>-reading implementation per feature-slice
    /// seam (node packages, notification channels, execution reads, polling triggers, schedules, settings).
    /// Each lets its slice depend on a Core interface instead of EF, keeping the slices seamed off persistence.
    /// </summary>
    public static IServiceCollection AddPersistenceAdapters(this IServiceCollection services)
    {
        services.AddScoped<INodePackageStore, DbNodePackageStore>();  // OpenApi import/delete handlers' Core seam
        services.AddScoped<INodePackageReadStore, DbNodePackageReadStore>();  // Nodes slice's runtime package read seam
        services.AddScoped<INotificationChannelStore, DbNotificationChannelStore>();  // Send Notification node + failure-alert resolver
        services.AddScoped<IExecutionReadStore, DbExecutionReadStore>();  // Notifications failure/error-workflow spines' run reads
        services.AddScoped<IPollingTriggerStore, DbPollingTriggerStore>();  // Polling slice's trigger read/cursor seam
        services.AddScoped<IScheduleStore, DbScheduleStore>();  // Schedules slice's due-read/advance seam
        services.AddScoped<ISettingsStore, DbSettingsStore>();  // Settings slice's Core seam
        return services;
    }
}
