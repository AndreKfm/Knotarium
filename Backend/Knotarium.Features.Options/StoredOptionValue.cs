using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Knotarium.Features.Options;

/// <summary>
/// Reads the stable key(s) a dynamic-options / resource-locator parameter persisted, ignoring the
/// display-only <c>label</c> cache. Tolerates legacy / forward-compat shapes so older saved
/// workflows keep resolving:
/// <list type="bullet">
///   <item>a bare string → a single manual value</item>
///   <item>a single object <c>{ value, label?, mode? }</c></item>
///   <item>a multi object <c>{ mode, items: [{ value, label? }, …] }</c></item>
///   <item>a bare array of strings or <c>{ value }</c> objects</item>
/// </list>
/// </summary>
public static class StoredOptionValue
{
    /// <summary>
    /// Extracts the stored stable keys in their persisted order. A single-valued shape yields a
    /// one-element list; an empty / null value yields an empty list. Never returns null entries.
    /// </summary>
    public static IReadOnlyList<string> ReadValues(object? stored)
    {
        if (stored is null) return Array.Empty<string>();

        var element = stored as JsonElement? ?? Normalize(stored);
        return ReadValues(element);
    }

    public static IReadOnlyList<string> ReadValues(JsonElement element)
    {
        var result = new List<string>();
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                AddIfPresent(result, element.GetString());
                break;

            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                AddIfPresent(result, element.GetRawText());
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AddIfPresent(result, ReadSingle(item));
                }
                break;

            case JsonValueKind.Object:
                if (element.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        AddIfPresent(result, ReadSingle(item));
                    }
                }
                else
                {
                    AddIfPresent(result, ReadSingle(element));
                }
                break;
        }

        return result;
    }

    /// <summary>Convenience for single-select params: first stored key or null.</summary>
    public static string? ReadSingleValue(object? stored)
    {
        var values = ReadValues(stored);
        return values.Count > 0 ? values[0] : null;
    }

    private static string? ReadSingle(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                return element.GetRawText();
            case JsonValueKind.Object:
                if (element.TryGetProperty("value", out var v))
                {
                    return v.ValueKind switch
                    {
                        JsonValueKind.String => v.GetString(),
                        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
                        _ => null,
                    };
                }
                return null;
            default:
                return null;
        }
    }

    private static void AddIfPresent(List<string> list, string? value)
    {
        if (!string.IsNullOrEmpty(value)) list.Add(value);
    }

    private static JsonElement Normalize(object value)
    {
        // Box arbitrary runtime values (string, dictionary, list) into a JsonElement for uniform reading.
        return JsonSerializer.SerializeToElement(value);
    }
}
