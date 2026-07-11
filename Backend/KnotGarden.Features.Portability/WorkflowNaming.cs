using System;
using System.Collections.Generic;

namespace KnotGarden.Features.Portability;

/// <summary>
/// Pure name-collision resolution for workflows. Duplicate names are allowed by the store, but importing
/// a template that produces yet another "First Sample" is confusing — so an imported workflow's name is
/// suffixed (<c>"… (2)"</c>, <c>"(3)"</c>, …) when it would collide with an existing one.
/// </summary>
public static class WorkflowNaming
{
    /// <summary>
    /// Returns <paramref name="desired"/> unchanged when it does not appear in <paramref name="existing"/>;
    /// otherwise appends the lowest free <c>" (n)"</c> suffix (n ≥ 2). Comparison is case-insensitive.
    /// </summary>
    public static string EnsureUnique(string desired, IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var name = string.IsNullOrWhiteSpace(desired) ? "Workflow" : desired.Trim();

        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
        {
            return name;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{name} ({n})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
