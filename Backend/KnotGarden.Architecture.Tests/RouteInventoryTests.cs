namespace KnotGarden.Architecture.Tests;

/// <summary>
/// Drift-control for the HTTP surface during (and after) the Program.cs modularization.
///
/// <para><b>Snapshot.</b> The full set of route templates the Api project registers must equal the
/// checked-in baseline. Extracting a handler out of Program.cs into an endpoint class is a pure
/// move — the <c>.Map*("literal")</c> call travels unchanged, so the surface stays byte-identical
/// and this test stays green. A genuine surface change (new route, renamed path, deleted endpoint)
/// requires editing <c>RouteInventory.baseline.txt</c> in the same commit — an explicit, reviewable act.</para>
///
/// <para><b>Ratchet.</b> Program.cs is the composition root and should trend toward zero inline route
/// registrations. <see cref="ProgramInlineRouteBudget"/> is an exact ceiling: extract N handlers and
/// lower it by N in the same commit. Adding an inline route (count &gt; budget) fails — extract it into
/// an endpoint class instead. Forgetting to lower the budget after an extraction (count &lt; budget)
/// also fails — tighten the ratchet. The target is 0.</para>
/// </summary>
public class RouteInventoryTests
{
    /// <summary>
    /// Inline <c>.Map*</c> registrations still allowed directly in Program.cs. Only ever lower this,
    /// in lockstep with an extraction. Started at 96 (pre-modularization); target 0.
    /// </summary>
    private const int ProgramInlineRouteBudget = 0;

    private static string BaselinePath =>
        Path.Combine(ModuleManifest.BackendRoot(), "KnotGarden.Architecture.Tests", "RouteInventory.baseline.txt");

    [Fact]
    public void Api_route_surface_matches_baseline()
    {
        var baseline = File.ReadAllLines(BaselinePath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var current = RouteScan.AllRoutes().ToHashSet(StringComparer.Ordinal);

        var added = current.Except(baseline).OrderBy(r => r, StringComparer.Ordinal).ToList();
        var removed = baseline.Except(current).OrderBy(r => r, StringComparer.Ordinal).ToList();

        Assert.True(added.Count == 0,
            "New route(s) not in the baseline. If intentional, add them to RouteInventory.baseline.txt; "
            + "if this is a refactor, the move changed a route template:\n  " + string.Join("\n  ", added));

        Assert.True(removed.Count == 0,
            "Route(s) in the baseline no longer registered. If intentional, remove them from "
            + "RouteInventory.baseline.txt; if this is a refactor, a handler was dropped or its template "
            + "changed:\n  " + string.Join("\n  ", removed));
    }

    [Fact]
    public void Program_cs_inline_routes_stay_within_budget()
    {
        var actual = RouteScan.ProgramInlineRouteCount();

        Assert.True(actual <= ProgramInlineRouteBudget,
            $"Program.cs registers {actual} inline routes, budget is {ProgramInlineRouteBudget}. "
            + "Extract new endpoints into an endpoint class rather than mapping them inline in the composition root.");

        Assert.True(actual == ProgramInlineRouteBudget,
            $"Program.cs now has {actual} inline routes but the budget is still {ProgramInlineRouteBudget}. "
            + $"Lower ProgramInlineRouteBudget to {actual} to tighten the ratchet (target: 0).");
    }
}
