// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Knotarium.Architecture.Tests;

/// <summary>
/// Source-text scan of the Knotarium.Api project for minimal-API route registrations.
/// A route registration is a literal <c>.MapGet("…")</c> / <c>.MapPost("…")</c> etc. call; the
/// scan reads the route template and HTTP verb straight out of the source, so it is faithful to a
/// pure-move refactor: relocating a handler verbatim from Program.cs into an endpoint class keeps
/// the same <c>.Map*("literal")</c> call, hence the same scanned entry. This is the safety net the
/// Program.cs modularization rests on — the HTTP surface must stay byte-identical as handlers move.
/// </summary>
internal static class RouteScan
{
    // Matches `.MapGet("…"`, `.MapPost("…"`, … capturing the verb and the (single-line) route literal.
    // Every route registration in this codebase puts the template literal on the same line as the Map call.
    private static readonly Regex MapCall = new(
        "\\.Map(Get|Post|Put|Delete|Patch)\\(\\s*\"([^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Absolute path to the Knotarium.Api project directory.</summary>
    public static string ApiProjectDir => Path.Combine(ModuleManifest.BackendRoot(), "Knotarium.Api");

    /// <summary>Every non-generated C# source file in the Api project.</summary>
    public static IEnumerable<string> ApiSourceFiles()
        => Directory.EnumerateFiles(ApiProjectDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>Route entries ("VERB /template") found in a single source file.</summary>
    public static IEnumerable<string> RoutesIn(string file)
        => MapCall.Matches(File.ReadAllText(file))
            .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}");

    /// <summary>The full HTTP surface of the Api project, sorted, de-duplicated.</summary>
    public static IReadOnlyList<string> AllRoutes()
    {
        var routes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in ApiSourceFiles())
            foreach (var route in RoutesIn(file))
                routes.Add(route);
        return routes.ToList();
    }

    /// <summary>Count of inline route registrations still living in Program.cs (the composition root).</summary>
    public static int ProgramInlineRouteCount()
        => RoutesIn(Path.Combine(ApiProjectDir, "Program.cs")).Count();
}
