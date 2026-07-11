namespace KnotGarden.Architecture.Tests;

/// <summary>
/// Layer 1 (drift-control): every production module's actual project references must conform to
/// the allowed/forbidden lists in its module.yaml. Freezes the project-level dependency DAG so a
/// stray &lt;ProjectReference&gt; fails the build.
/// </summary>
public class ManifestConformanceTests
{
    [Fact]
    public void Every_module_references_only_allowed_projects()
    {
        var problems = new List<string>();

        foreach (var module in ModuleManifest.LoadProductionModules())
        {
            var allowed = module.AllowedProjectDependencies.ToHashSet(StringComparer.Ordinal);
            var forbidden = module.ForbiddenProjectDependencies.ToHashSet(StringComparer.Ordinal);

            foreach (var reference in module.ActualProjectReferences)
            {
                if (!allowed.Contains(reference))
                    problems.Add($"{module.Name}: references '{reference}' which is not in allowed_project_dependencies");
                if (forbidden.Contains(reference))
                    problems.Add($"{module.Name}: references '{reference}' which is in forbidden_project_dependencies");
            }
        }

        Assert.True(problems.Count == 0,
            "Project references violate module.yaml:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void At_least_the_extracted_leaf_projects_are_present()
    {
        // Guards against the loader silently finding nothing (e.g. path resolution breaking).
        var names = ModuleManifest.LoadProductionModules().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
                 {
                     "KnotGarden.Core", "KnotGarden.Features",
                     "KnotGarden.Features.Options", "KnotGarden.Features.NodeEditor", "KnotGarden.Features.Compiler",
                     "KnotGarden.Features.Settings", "KnotGarden.Features.Condition", "KnotGarden.Features.OpenApi",
                     "KnotGarden.Features.Portability", "KnotGarden.Features.Ai",
                 })
            Assert.Contains(expected, names);
    }
}
