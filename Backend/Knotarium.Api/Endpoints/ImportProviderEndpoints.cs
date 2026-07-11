using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Knotarium.Features.Bundles;
using Knotarium.Features.Execution;
using Knotarium.Features.Portability;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Api;

/// <summary>
/// Vendor-setting import (host hook §8): a generic surface over plugin-contributed
/// IWorkflowImportProvider capabilities — list providers, preview an uploaded file's coverage
/// report, and install the generated workflows as inactive versions. The host never sees vendor
/// types; only generic WorkflowDefinition + a report cross the seam.
/// </summary>
public static class ImportProviderEndpoints
{
    public static void MapImportProviderEndpoints(this WebApplication app)
    {
        app.MapGet("/api/imports/providers", (Knotarium.NodeRuntime.HostPluginRegistry plugins) =>
            Results.Ok(plugins.ImportProviders.Select(p => new
            {
                p.Descriptor.Id,
                p.Descriptor.DisplayName,
                p.Descriptor.FileExtensions,
                p.Descriptor.SupportsGranularity,
                p.Descriptor.SupportsTargetStrategy,
                p.Descriptor.DefaultGranularity,
                p.Descriptor.Description,
            })));

        app.MapPost("/api/imports/{providerId}/preview", async (
            string providerId,
            HttpRequest request,
            Knotarium.NodeRuntime.HostPluginRegistry plugins) =>
        {
            var provider = plugins.ImportProviders.FirstOrDefault(p =>
                string.Equals(p.Descriptor.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return Results.NotFound(new { message = $"No import provider '{providerId}'." });
            if (!request.HasFormContentType) return Results.BadRequest(new { message = "Expected a multipart file upload." });

            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Results.BadRequest(new { message = "No file uploaded." });
            var granularity = NormalizeGranularity(form["granularity"]);
            // Preview never provisions (Provision=false), so a strategy/map can be supplied to see what WOULD happen.
            var req = new Knotarium.Core.Contracts.WorkflowImportRequest(
                granularity, NormalizeStrategy(form["targetStrategy"]), ParseServerMap(form["serverMappings"]), Provision: false);

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);

            try
            {
                var result = provider.Import(ms.ToArray(), Path.GetExtension(file.FileName).TrimStart('.'), req);
                return Results.Ok(new
                {
                    granularity,
                    workflows = result.Workflows.Select(w => new { id = w.Id.Value, name = w.Name, nodes = w.Nodes.Count, edges = w.Edges.Count }),
                    report = ReportRows(result),
                    servers = ServerRows(result),
                    provisioned = ProvisionRows(result),
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { message = $"Import failed: {ex.Message}" });
            }
        });

        app.MapPost("/api/imports/{providerId}/install", async (
            string providerId,
            HttpRequest request,
            Knotarium.NodeRuntime.HostPluginRegistry plugins,
            WorkflowPublisher workflowPublisher,
            AppDbContext db) =>
        {
            var provider = plugins.ImportProviders.FirstOrDefault(p =>
                string.Equals(p.Descriptor.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (provider is null) return Results.NotFound(new { message = $"No import provider '{providerId}'." });
            if (!request.HasFormContentType) return Results.BadRequest(new { message = "Expected a multipart file upload." });

            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Results.BadRequest(new { message = "No file uploaded." });
            var granularity = NormalizeGranularity(form["granularity"]);
            var req = new Knotarium.Core.Contracts.WorkflowImportRequest(
                granularity, NormalizeStrategy(form["targetStrategy"]), ParseServerMap(form["serverMappings"]), Provision: true);

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);

            Knotarium.Core.Contracts.WorkflowImportProviderResult result;
            try
            {
                result = provider.Import(ms.ToArray(), Path.GetExtension(file.FileName).TrimStart('.'), req);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { message = $"Import failed: {ex.Message}" });
            }

            // Provider-assigned workflow ids can collide across imports — e.g. every single-file vendor-setting import
            // uses the same fixed id. Left as-is, a second setting would land as a hidden inactive *version* of the
            // first (ImportAsync keys on the id) and never show as its own workflow. Give each colliding workflow a
            // fresh id + disambiguated name so distinct settings install as distinct workflows. The set is seeded
            // from the DB and grows as we install, so several workflows in one upload can't collide with each other.
            var existingIds = new HashSet<string>(
                await db.WorkflowDefinitions.Select(w => w.Id.Value).ToListAsync(), StringComparer.OrdinalIgnoreCase);
            var existingNames = new HashSet<string>(
                await db.WorkflowDefinitions.Select(w => w.Name).ToListAsync(), StringComparer.OrdinalIgnoreCase);

            var installed = new List<object>();
            foreach (var workflow in result.Workflows)
            {
                var toInstall = workflow;
                if (existingIds.Contains(toInstall.Id.Value))
                {
                    string newId;
                    do { newId = $"{toInstall.Id.Value}-{Guid.NewGuid():N}"[..Math.Min(toInstall.Id.Value.Length + 9, 96)]; }
                    while (existingIds.Contains(newId));
                    toInstall = toInstall with
                    {
                        Id = new Knotarium.Core.Domain.WorkflowDefinitionId(newId),
                        Name = DisambiguateWorkflowName(toInstall.Name, existingNames),
                    };
                }
                existingIds.Add(toInstall.Id.Value);
                existingNames.Add(toInstall.Name);

                var imported = await workflowPublisher.ImportAsync(ToExportDocument(toInstall));
                installed.Add(new { toInstall.Id.Value, name = toInstall.Name, versionNumber = imported.Version.VersionNumber });
            }

            return Results.Ok(new
            {
                granularity,
                installed,
                report = ReportRows(result),
                servers = ServerRows(result),
                provisioned = ProvisionRows(result),
            });
        });
    }

    private static string NormalizeGranularity(string? value) =>
        string.Equals(value, "single", StringComparison.OrdinalIgnoreCase) ? "single" : "multiple";

    private static string NormalizeStrategy(string? value) => (value ?? string.Empty).ToLowerInvariant() switch
    {
        "maptoexisting" or "map" => "MapToExisting",
        "dontmap" or "none" => "DontMap",
        _ => "CreateOrReuse",
    };

    private static IReadOnlyDictionary<string, string>? ParseServerMap(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json!);

    private static WorkflowExportDocument ToExportDocument(Knotarium.Core.Domain.WorkflowDefinition def) =>
        new(new WorkflowExportManifest(def.Id.Value, def.Name, 1, "Imported", null, string.Empty),
            new WorkflowExportContent(def.Nodes, def.Edges));

    // Append " (N)" until the name is free, so two imports of the same-named setting stay tellable apart.
    private static string DisambiguateWorkflowName(string baseName, ISet<string> taken)
    {
        if (!taken.Contains(baseName)) return baseName;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName} ({n})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    private static IEnumerable<object> ReportRows(Knotarium.Core.Contracts.WorkflowImportProviderResult r) =>
        r.Report.Entries.Select(e => new { e.Scope, e.Construct, outcome = e.Outcome.ToString(), e.Reason });
    private static IEnumerable<object> ServerRows(Knotarium.Core.Contracts.WorkflowImportProviderResult r) =>
        r.DiscoveredServers.Select(s => new { s.Alias, s.Host, s.User, s.Enabled });
    private static IEnumerable<object> ProvisionRows(Knotarium.Core.Contracts.WorkflowImportProviderResult r) =>
        r.ProvisionedTargets.Select(t => new { t.ServerAlias, t.Action, t.TargetId });
}
