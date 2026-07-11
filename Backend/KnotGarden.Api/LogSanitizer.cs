using System;
using System.Collections.Generic;
using KnotGarden.Core.Domain;

namespace KnotGarden.Api;

internal static class LogSanitizer
{
    public static Dictionary<string, object> MaskDictionary(Dictionary<string, object> source)
    {
        var result = new Dictionary<string, object>(source.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in source)
        {
            if (kvp.Value is SecretValue)
            {
                result[kvp.Key] = "***";
                continue;
            }

            result[kvp.Key] = IsSensitiveKey(kvp.Key) ? "***" : kvp.Value;
        }

        return result;
    }

    private static bool IsSensitiveKey(string key)
    {
        return key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || key.Contains("authorization", StringComparison.OrdinalIgnoreCase);
    }
}
