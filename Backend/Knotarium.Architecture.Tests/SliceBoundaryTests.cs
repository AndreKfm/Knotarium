namespace Knotarium.Architecture.Tests;

/// <summary>
/// Layer 2 (drift-control): a ratchet over the feature slices that still share the single
/// Knotarium.Features assembly. New cross-slice edges (or new direct AppDbContext users) fail
/// the build; fixing one requires removing it from the baseline in Features/module.yaml. The
/// baselines may only shrink — the target is empty (both cycles gone, all persistence behind seams).
/// </summary>
public class SliceBoundaryTests
{
    private static SliceRules LoadRules()
    {
        var features = ModuleManifest.LoadProductionModules()
            .Single(m => m.Name == "Knotarium.Features");
        return features.SliceRules
            ?? throw new InvalidOperationException("Features/module.yaml is missing slice_rules.");
    }

    [Fact]
    public void No_new_cross_slice_edges()
    {
        var baseline = (LoadRules().BaselineSliceEdges ?? new()).ToHashSet(StringComparer.Ordinal);
        var current = SliceScan.CurrentEdges().ToHashSet(StringComparer.Ordinal);

        var introduced = current.Except(baseline).OrderBy(e => e, StringComparer.Ordinal).ToList();
        var fixedAlready = baseline.Except(current).OrderBy(e => e, StringComparer.Ordinal).ToList();

        Assert.True(introduced.Count == 0,
            "New cross-slice edge(s) introduced — route through a Core interface instead:\n  "
            + string.Join("\n  ", introduced));

        Assert.True(fixedAlready.Count == 0,
            "These baseline edges no longer exist — delete them from slice_rules.baseline_slice_edges (ratchet):\n  "
            + string.Join("\n  ", fixedAlready));
    }

    [Fact]
    public void No_new_direct_AppDbContext_users()
    {
        var baseline = (LoadRules().BaselineAppdbcontextUsers ?? new()).ToHashSet(StringComparer.Ordinal);
        var current = SliceScan.AppDbContextUsers().ToHashSet(StringComparer.Ordinal);

        var introduced = current.Except(baseline).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var fixedAlready = baseline.Except(current).OrderBy(s => s, StringComparer.Ordinal).ToList();

        Assert.True(introduced.Count == 0,
            "Slice(s) now inject AppDbContext directly — use a Core store/repository seam instead:\n  "
            + string.Join("\n  ", introduced));

        Assert.True(fixedAlready.Count == 0,
            "These slices no longer use AppDbContext — delete them from slice_rules.baseline_appdbcontext_users (ratchet):\n  "
            + string.Join("\n  ", fixedAlready));
    }
}
