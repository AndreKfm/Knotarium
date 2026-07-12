using System;
using System.IO;
using System.Text.Json;

namespace KnotGarden.Tests.Nodes.Condition;

/// <summary>
/// Loads the shared B2 fixtures (linked into the test output from test-fixtures/condition — the same files the
/// frontend suite consumes). Centralizes the path so both the evaluator and catalog tests agree.
/// </summary>
internal static class ConditionFixtures
{
    private static readonly string Dir =
        Path.Combine(AppContext.BaseDirectory, "Condition", "Fixtures");

    public static JsonDocument Load(string fileName)
    {
        var path = Path.Combine(Dir, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Condition fixture '{fileName}' not found at '{path}'. " +
                "Ensure test-fixtures/condition/*.fixture.json is linked into KnotGarden.Tests.csproj as copied Content.",
                path);
        }
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    public const string Conformance = "condition-conformance.fixture.json";
    public const string Catalog = "condition-catalog.fixture.json";
    public const string Tree = "condition-tree.fixture.json";
}
