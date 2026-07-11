using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts.Options;

namespace KnotGarden.Features.Options;

/// <summary>The outcome of resolving stored stable key(s) against the live resource list.</summary>
public sealed record ResourceResolution(IReadOnlyList<OptionItem> Resources);

/// <summary>
/// Thrown when a stored key cannot be resolved against the current resource list — a deleted /
/// renamed resource, or an ambiguous name-typed key. Resolution is <b>fail-closed</b>: the node
/// fails with this rather than silently skipping or guessing.
/// </summary>
public sealed class ResourceResolutionException : Exception
{
    public IReadOnlyList<string> MissingKeys { get; }
    public IReadOnlyList<string> AmbiguousKeys { get; }

    public ResourceResolutionException(string message, IReadOnlyList<string> missingKeys, IReadOnlyList<string> ambiguousKeys)
        : base(message)
    {
        MissingKeys = missingKeys;
        AmbiguousKeys = ambiguousKeys;
    }
}

/// <summary>
/// Shared execution-time resolver: turns the stored stable key(s) of a dynamic-options parameter
/// into live resource handles. Every node using a dynamic param resolves the same way, so new
/// integrations reuse this instead of baking resolution into a specific node.
/// </summary>
public sealed class ResourceResolver
{
    private readonly IOptionsLoaderRegistry _registry;

    public ResourceResolver(IOptionsLoaderRegistry registry) => _registry = registry;

    /// <summary>
    /// Re-reads the live list <b>once</b>, builds a stableKey → entry map, and indexes the stored
    /// key(s) into it (single value or array, input order preserved). Because lookup is associative
    /// — <c>resources["res_7f3a"]</c>, not positional — upstream reordering cannot retarget the
    /// workflow. Any unresolvable or ambiguous key fails the whole resolution (fail-closed).
    /// </summary>
    public async Task<ResourceResolution> ResolveAsync(
        string loaderName,
        object? storedValue,
        OptionLoadContext context,
        CancellationToken cancellationToken)
    {
        var keys = StoredOptionValue.ReadValues(storedValue);
        if (keys.Count == 0)
        {
            return new ResourceResolution(Array.Empty<OptionItem>());
        }

        var loader = _registry.Get(loaderName)
            ?? throw new ResourceResolutionException(
                $"Unknown options loader '{loaderName}'.", Array.Empty<string>(), Array.Empty<string>());

        // Single live fetch — never per-key (avoids N+1).
        var live = await loader.LoadAsync(context, cancellationToken);

        // stableKey → entry, plus duplicate detection for ambiguous name-typed keys.
        var byKey = new Dictionary<string, OptionItem>(StringComparer.Ordinal);
        var duplicated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in live.Options)
        {
            if (!byKey.TryAdd(item.Value, item))
            {
                duplicated.Add(item.Value);
            }
        }

        var resolved = new List<OptionItem>(keys.Count);
        var missing = new List<string>();
        var ambiguous = new List<string>();

        foreach (var key in keys)
        {
            if (duplicated.Contains(key))
            {
                ambiguous.Add(key);
            }
            else if (byKey.TryGetValue(key, out var entry))
            {
                resolved.Add(entry);
            }
            else
            {
                missing.Add(key);
            }
        }

        if (missing.Count > 0 || ambiguous.Count > 0)
        {
            throw new ResourceResolutionException(BuildMessage(missing, ambiguous), missing, ambiguous);
        }

        return new ResourceResolution(resolved);
    }

    private static string BuildMessage(IReadOnlyList<string> missing, IReadOnlyList<string> ambiguous)
    {
        var parts = new List<string>();
        if (missing.Count > 0)
        {
            parts.Add($"could not be found (deleted or renamed): {string.Join(", ", missing)}");
        }
        if (ambiguous.Count > 0)
        {
            parts.Add($"are ambiguous (multiple resources share the name): {string.Join(", ", ambiguous)}");
        }
        return $"Cannot resolve referenced resource(s) — {string.Join("; ", parts)}.";
    }
}
