using System;
using System.Collections.Generic;
using System.Linq;
using KnotGarden.Core.Contracts.OpenApi;
using KnotGarden.Core.Domain.OpenApi;

namespace KnotGarden.Features.OpenApi;

/// <summary>
/// A spec-derived hint that a path parameter can be picked from a live resource list rather than
/// typed. <see cref="CollectionPath"/> is the sibling collection endpoint (may itself contain
/// <c>{placeholder}</c> segments resolved from <see cref="DependsOn"/> at load time).
/// </summary>
public sealed record LocatorSuggestion(
    string Name,
    string In,
    string CollectionPath,
    string ValueField,
    string LabelField,
    IReadOnlyList<string> DependsOn
);

/// <summary>
/// Infers resource-locator suggestions for an operation's path parameters by pattern-matching the
/// OpenAPI paths — no response-schema introspection (the parsed model doesn't carry it). For a path
/// like <c>GET /pets/{id}</c> it looks for a sibling <c>GET /pets</c> collection; for a nested
/// <c>GET /stores/{storeId}/pets/{petId}</c> it suggests <c>GET /stores/{storeId}/pets</c> and marks
/// <c>storeId</c> as a cascading dependency. Pure transformation — no I/O.
/// </summary>
public static class ResourceLocatorInference
{
    public static IReadOnlyList<LocatorSuggestion> Suggest(ParsedSpec spec, ApiOperation operation)
    {
        // All GET collection paths in the spec, for parent-path matching.
        var getPaths = new HashSet<string>(
            spec.Operations
                .Where(o => string.Equals(o.Method, "GET", StringComparison.OrdinalIgnoreCase))
                .Select(o => Normalize(o.PathTemplate)),
            StringComparer.OrdinalIgnoreCase);

        var suggestions = new List<LocatorSuggestion>();
        var segments = SplitSegments(operation.PathTemplate);

        foreach (var param in operation.Parameters.Where(p => string.Equals(p.In, "path", StringComparison.OrdinalIgnoreCase)))
        {
            var token = "{" + param.Name + "}";
            var index = Array.FindIndex(segments, s => string.Equals(s, token, StringComparison.OrdinalIgnoreCase));
            if (index <= 0) continue; // not found, or the resource id is the very first segment (no parent collection)

            var collectionPath = "/" + string.Join("/", segments.Take(index));
            if (!getPaths.Contains(Normalize(collectionPath))) continue; // no sibling collection to list from

            // Other path placeholders inside the collection path become cascading dependencies.
            var dependsOn = operation.Parameters
                .Where(p => !string.Equals(p.Name, param.Name, StringComparison.Ordinal)
                    && string.Equals(p.In, "path", StringComparison.OrdinalIgnoreCase)
                    && collectionPath.Contains("{" + p.Name + "}", StringComparison.Ordinal))
                .Select(p => p.Name)
                .ToList();

            suggestions.Add(new LocatorSuggestion(
                param.Name, "path", collectionPath, ValueField: "id", LabelField: "name", dependsOn));
        }

        return suggestions;
    }

    private static string[] SplitSegments(string pathTemplate) =>
        pathTemplate.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Normalize(string path) => "/" + string.Join("/", SplitSegments(path));
}
