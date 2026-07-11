using System.Collections.Generic;
using System.Text.Json;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Reads subflow input/output mapping lists off a node property. The value may arrive already
/// evaluated by the executor (a list of dictionaries) or as a raw <see cref="JsonElement"/> array
/// (e.g. straight from a definition or in unit tests), so both shapes are tolerated.
/// </summary>
internal static class SubflowMapping
{
    /// <summary>Yields (text-of-keyA, raw-value-of-keyB) for each entry.</summary>
    public static IEnumerable<(string? name, object? value)> ReadEntries(object? raw, string keyA, string keyB)
    {
        foreach (var item in Enumerate(raw))
        {
            yield return (GetString(item, keyA), GetValue(item, keyB));
        }
    }

    /// <summary>Yields (text-of-keyA, text-of-keyB) for each entry.</summary>
    public static IEnumerable<(string? first, string? second)> ReadPairs(object? raw, string keyA, string keyB)
    {
        foreach (var item in Enumerate(raw))
        {
            yield return (GetString(item, keyA), GetString(item, keyB));
        }
    }

    private static IEnumerable<object?> Enumerate(object? raw)
    {
        if (raw is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in element.EnumerateArray())
            {
                yield return entry;
            }
            yield break;
        }

        if (raw is IEnumerable<object?> list)
        {
            foreach (var entry in list)
            {
                yield return entry;
            }
        }
    }

    private static string? GetString(object? item, string key)
    {
        var value = GetValue(item, key);
        if (value is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();
        }
        return value?.ToString();
    }

    private static object? GetValue(object? item, string key)
    {
        switch (item)
        {
            case IReadOnlyDictionary<string, object> roDict when TryGet(roDict, key, out var v):
                return v;
            case IDictionary<string, object> dict when dict.TryGetValue(key, out var v):
                return v;
            case JsonElement je when je.ValueKind == JsonValueKind.Object && TryGetJson(je, key, out var jv):
                return jv;
            default:
                return null;
        }
    }

    private static bool TryGet(IReadOnlyDictionary<string, object> dict, string key, out object? value)
    {
        foreach (var kvp in dict)
        {
            if (string.Equals(kvp.Key, key, System.StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static bool TryGetJson(JsonElement obj, string key, out object? value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, key, System.StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = null;
        return false;
    }
}
