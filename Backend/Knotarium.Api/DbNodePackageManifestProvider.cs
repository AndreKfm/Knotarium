using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Knotarium.Infrastructure.Persistence;
using Knotarium.NodeRuntime;

namespace Knotarium.Api;

public class DbNodePackageManifestProvider : INodePackageManifestProvider, INodePackageCatalogProvider
{
    private static readonly JsonSerializerOptions ManifestSerializerOptions = CreateManifestSerializerOptions();

    private readonly InMemoryNodePackageManifestProvider _inMemoryProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INodeExecutorRegistry _binaryRegistry;

    public DbNodePackageManifestProvider(
        InMemoryNodePackageManifestProvider inMemoryProvider,
        IServiceScopeFactory scopeFactory,
        INodeExecutorRegistry binaryRegistry)
    {
        _inMemoryProvider = inMemoryProvider;
        _scopeFactory = scopeFactory;
        _binaryRegistry = binaryRegistry;
    }

    public async Task<NodePackageManifest?> GetManifestAsync(NodePackageId id, CancellationToken cancellationToken = default)
    {
        // 1. Try built-ins first
        var manifest = await _inMemoryProvider.GetManifestAsync(id, cancellationToken);
        if (manifest != null) return manifest;

        // 2. Then prebuilt binary packages loaded from disk
        var binary = _binaryRegistry.GetLatest(id);
        if (binary != null) return binary.Manifest;

        // 3. Query database for custom package manifests
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pkg = await db.NodePackages
            .Include(p => p.Versions)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (pkg == null || pkg.Versions == null || pkg.Versions.Count == 0) return null;

        // Get the latest version
        var latestVersion = pkg.Versions
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefault();

        if (latestVersion == null || string.IsNullOrWhiteSpace(latestVersion.ManifestJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<NodePackageManifest>(latestVersion.ManifestJson, ManifestSerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<NodePackageRegistryItem>> GetNodePackagesAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var databasePackages = await db.NodePackages
            .Include(package => package.Versions)
            .OrderBy(package => package.DisplayName)
            .ToListAsync(cancellationToken);

        var builtInPackages = _inMemoryProvider
            .GetAllManifests()
            .OrderBy(manifest => manifest.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(manifest => new NodePackageRegistryItem(
                manifest.Id.Value,
                manifest.DisplayName,
                manifest.Category,
                new[]
                {
                    new NodePackageRegistryVersion(
                        Guid.Empty,
                        manifest.Id.Value,
                        manifest.Version,
                        JsonSerializer.Serialize(manifest, ManifestSerializerOptions),
                        $"Built-in {manifest.Tier}",
                        manifest.Capabilities,
                        DateTimeOffset.UnixEpoch)
                }))
            .ToList();

        var customPackages = databasePackages
            .Select(package => new NodePackageRegistryItem(
                package.Id.Value,
                package.DisplayName,
                package.Category,
                package.Versions
                    .OrderByDescending(version => version.CreatedAt)
                    .Select(version => new NodePackageRegistryVersion(
                        version.Id.Value,
                        version.NodePackageId.Value,
                        version.Version,
                        version.ManifestJson,
                        version.Source,
                        version.Capabilities,
                        version.CreatedAt))
                    .ToList()))
            .ToList();

        // Prebuilt binary packages loaded from disk. Surfaced like built-ins so they appear in
        // the editor palette and resolve during compilation; their executor lives in the registry.
        var binaryPackages = _binaryRegistry
            .GetAll()
            .Select(reg => reg.Manifest)
            .GroupBy(manifest => manifest.Id.Value)
            .Select(group => group.OrderByDescending(m => m.Version, StringComparer.OrdinalIgnoreCase).First())
            .Select(manifest => new NodePackageRegistryItem(
                manifest.Id.Value,
                manifest.DisplayName,
                manifest.Category,
                new[]
                {
                    new NodePackageRegistryVersion(
                        Guid.Empty,
                        manifest.Id.Value,
                        manifest.Version,
                        JsonSerializer.Serialize(manifest, ManifestSerializerOptions),
                        $"Binary {manifest.Tier}",
                        manifest.Capabilities,
                        DateTimeOffset.UnixEpoch)
                }))
            .ToList();

        // De-dupe by id with precedence built-in < binary < custom: a deployed package (e.g. a branded
        // device block) overrides a placeholder built-in of the same id, so the palette and the editor's
        // manifest lookup see one authoritative entry rather than a shadowing pair.
        var byId = new Dictionary<string, NodePackageRegistryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in builtInPackages.Concat(binaryPackages).Concat(customPackages))
        {
            byId[item.Id] = item;
        }

        return byId.Values
            .OrderBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// All available manifests (built-ins + deployed binary packages + DB packages), deserialized from the
    /// unified registry. Used to ground AI workflow generation so the model can select any deployed node
    /// type, not just the built-ins. A package whose manifest JSON is malformed is skipped rather than
    /// failing the whole catalog.
    /// </summary>
    public async Task<IReadOnlyList<NodePackageManifest>> GetAllManifestsAsync(CancellationToken cancellationToken = default)
    {
        var packages = await GetNodePackagesAsync(cancellationToken);
        var manifests = new List<NodePackageManifest>(packages.Count);
        foreach (var package in packages)
        {
            // GetNodePackagesAsync orders each package's versions latest-first.
            var latest = package.Versions.Count > 0 ? package.Versions[0] : null;
            if (latest is null || string.IsNullOrWhiteSpace(latest.ManifestJson)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<NodePackageManifest>(latest.ManifestJson, ManifestSerializerOptions);
                if (manifest is not null) manifests.Add(manifest);
            }
            catch (JsonException)
            {
                // Skip a malformed package manifest — never let one bad package break generation.
            }
        }
        return manifests;
    }

    private static JsonSerializerOptions CreateManifestSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
