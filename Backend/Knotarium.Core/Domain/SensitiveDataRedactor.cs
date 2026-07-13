using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;

namespace Knotarium.Core.Domain;

/// <summary>
/// Masks secret-looking values in the untyped JSON blobs that runs persist (node inputs/outputs,
/// variable snapshots, journal payloads) so they are not handed back verbatim over the read APIs. Redaction
/// is applied on READ (the API boundary), leaving the stored data intact for replay/debugging while ensuring
/// a runtime-fetched or derived secret is never returned to a dashboard client. Masking is key-based (the
/// name looks sensitive) plus type-based (a <see cref="SecretValue"/>), and recurses through nested
/// dictionaries, lists, and <see cref="JsonElement"/> objects/arrays so a secret nested inside a blob is
/// caught too.
/// </summary>
public static class SensitiveDataRedactor
{
    public const string Mask = "***";

    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }
        return key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || key.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            || key.Contains("authorization", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns a new dictionary with sensitive values masked; the input is not mutated. Null-safe.</summary>
    public static Dictionary<string, object> Redact(IReadOnlyDictionary<string, object>? source)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (source is null)
        {
            return result;
        }

        foreach (var kvp in source)
        {
            result[kvp.Key] = IsSensitiveKey(kvp.Key) ? Mask : RedactValue(kvp.Value);
        }
        return result;
    }

    /// <summary>Redacts a raw JSON string (e.g. a stored variable snapshot). Returns the input unchanged if it
    /// is null/empty or not parseable as JSON.</summary>
    public static string? RedactJsonString(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var redacted = RedactJsonElement(document.RootElement);
            return JsonSerializer.Serialize(redacted);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static object RedactValue(object? value)
    {
        switch (value)
        {
            case null:
                return null!;
            case SecretValue:
                return Mask;
            case string s:
                return s;
            case JsonElement element:
                return RedactJsonElement(element);
            case IReadOnlyDictionary<string, object> readOnlyDict:
                return Redact(readOnlyDict);
            case IDictionary<string, object> dict:
                return Redact(new Dictionary<string, object>(dict));
            case IEnumerable enumerable when value is not string:
            {
                var list = new List<object>();
                foreach (var item in enumerable)
                {
                    list.Add(RedactValue(item));
                }
                return list;
            }
            default:
                return value;
        }
    }

    private static object RedactJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var obj = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    obj[property.Name] = IsSensitiveKey(property.Name) ? Mask : RedactJsonElement(property.Value);
                }
                return obj;
            }
            case JsonValueKind.Array:
            {
                var list = new List<object>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(RedactJsonElement(item));
                }
                return list;
            }
            default:
                // Primitive JSON value — return it unchanged (it was reached under a non-sensitive key).
                return element;
        }
    }
}
