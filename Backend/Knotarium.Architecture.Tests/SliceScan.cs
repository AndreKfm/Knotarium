using System.Reflection;
using NetArchTest.Rules;

namespace Knotarium.Architecture.Tests;

/// <summary>
/// IL-level (Mono.Cecil via NetArchTest) queries over the Knotarium.Features assembly.
/// Reflection alone misses static calls inside method bodies; NetArchTest reads the IL, so it
/// catches every real type dependency — including fully-qualified refs and static helper calls.
/// </summary>
internal static class SliceScan
{
    /// <summary>The feature slices still living inside the single Knotarium.Features assembly.</summary>
    public static readonly string[] InFeaturesSlices =
    {
        "Execution", "Nodes", "Notifications",
        "Polling", "Reactive", "Schedules",
    };

    public static Assembly FeaturesAssembly => typeof(Knotarium.Features.Nodes.InlineCodeNodeTask).Assembly;

    private static string Ns(string slice) => $"Knotarium.Features.{slice}";

    /// <summary>True if any type in slice <paramref name="from"/> depends on a type in slice <paramref name="to"/>.</summary>
    public static bool HasEdge(string from, string to)
    {
        var result = Types.InAssembly(FeaturesAssembly)
            .That().ResideInNamespace(Ns(from))
            .Should().NotHaveDependencyOn(Ns(to))
            .GetResult();
        return !result.IsSuccessful;
    }

    /// <summary>All current cross-slice edges among the in-Features slices, as "From->To" strings.</summary>
    public static IReadOnlyList<string> CurrentEdges()
    {
        var edges = new List<string>();
        foreach (var from in InFeaturesSlices)
            foreach (var to in InFeaturesSlices)
            {
                if (from == to) continue;
                if (HasEdge(from, to)) edges.Add($"{from}->{to}");
            }
        edges.Sort(StringComparer.Ordinal);
        return edges;
    }

    /// <summary>Slices whose types depend on the shared EF AppDbContext.</summary>
    public static IReadOnlyList<string> AppDbContextUsers()
    {
        const string appDbContext = "Knotarium.Infrastructure.Persistence.AppDbContext";
        var users = new List<string>();
        foreach (var slice in InFeaturesSlices)
        {
            var result = Types.InAssembly(FeaturesAssembly)
                .That().ResideInNamespace(Ns(slice))
                .Should().NotHaveDependencyOn(appDbContext)
                .GetResult();
            if (!result.IsSuccessful) users.Add(slice);
        }
        users.Sort(StringComparer.Ordinal);
        return users;
    }
}
