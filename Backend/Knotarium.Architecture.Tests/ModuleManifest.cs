// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Knotarium.Architecture.Tests;

/// <summary>Parsed view of a backend module.yaml plus the project references from its .csproj.</summary>
internal sealed class ModuleManifest
{
    public required string Name { get; init; }
    public required string CsprojPath { get; init; }
    public required IReadOnlyList<string> AllowedProjectDependencies { get; init; }
    public required IReadOnlyList<string> ForbiddenProjectDependencies { get; init; }
    public required IReadOnlyList<string> ActualProjectReferences { get; init; }
    public SliceRules? SliceRules { get; init; }

    /// <summary>Walk up from the test binary to the Backend folder (the one holding Knotarium.slnx).</summary>
    public static string BackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Knotarium.slnx")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("Could not locate Backend root (Knotarium.slnx).");
        return dir.FullName;
    }

    /// <summary>Load every non-test module manifest under Backend that has both a module.yaml and a .csproj.</summary>
    public static IReadOnlyList<ModuleManifest> LoadProductionModules()
    {
        var backend = BackendRoot();
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var manifests = new List<ModuleManifest>();
        foreach (var dir in Directory.GetDirectories(backend))
        {
            var dirName = Path.GetFileName(dir);
            // Test projects are out of scope for production conformance. Matches both the ".Tests"
            // suffix and the ".Tests.Api" integration-host project.
            if (dirName.Contains(".Tests", StringComparison.Ordinal)) continue;

            var manifestPath = Path.Combine(dir, "module.yaml");
            var csproj = Directory.GetFiles(dir, "*.csproj").FirstOrDefault();
            if (!File.Exists(manifestPath) || csproj is null) continue;

            var doc = deserializer.Deserialize<ManifestFile>(File.ReadAllText(manifestPath));
            var m = doc?.Module ?? throw new InvalidOperationException($"Empty module.yaml at {manifestPath}");

            manifests.Add(new ModuleManifest
            {
                Name = m.Name ?? dirName,
                CsprojPath = csproj,
                AllowedProjectDependencies = m.AllowedProjectDependencies ?? new(),
                ForbiddenProjectDependencies = m.ForbiddenProjectDependencies ?? new(),
                ActualProjectReferences = ParseProjectReferences(csproj),
                SliceRules = m.SliceRules,
            });
        }
        return manifests;
    }

    private static IReadOnlyList<string> ParseProjectReferences(string csprojPath)
    {
        var text = File.ReadAllText(csprojPath);
        return Regex.Matches(text, "ProjectReference\\s+Include=\"([^\"]+)\"")
            .Select(match => Path.GetFileNameWithoutExtension(match.Groups[1].Value.Replace('\\', '/')))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    // ---- YAML shape (subset; unmatched keys ignored) ----
    private sealed class ManifestFile { public ModuleNode? Module { get; set; } }

    private sealed class ModuleNode
    {
        public string? Name { get; set; }
        public List<string>? AllowedProjectDependencies { get; set; }
        public List<string>? ForbiddenProjectDependencies { get; set; }
        public SliceRules? SliceRules { get; set; }
    }
}

internal sealed class SliceRules
{
    public List<string>? BaselineSliceEdges { get; set; }
    public List<string>? BaselineAppdbcontextUsers { get; set; }
}
