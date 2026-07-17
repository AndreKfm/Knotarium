// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text.Json;

namespace Knotarium.Features.Notifications;

/// <summary>Small helpers for reading transport-specific values out of a decrypted channel config.</summary>
internal static class NotificationConfig
{
    public static string? GetString(JsonElement config, string property)
    {
        if (config.ValueKind == JsonValueKind.Object &&
            config.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    public static int? GetInt(JsonElement config, string property)
    {
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty(property, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public static bool GetBool(JsonElement config, string property, bool defaultValue = false)
    {
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty(property, out var value))
        {
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    public static IReadOnlyList<string> GetStringList(JsonElement config, string property)
    {
        var result = new List<string>();
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty(property, out var value))
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var str = item.GetString();
                        if (!string.IsNullOrWhiteSpace(str))
                        {
                            result.Add(str);
                        }
                    }
                }
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                // Tolerate a comma/semicolon-separated string for convenience (e.g. e-mail recipients).
                foreach (var part in value.GetString()!.Split(new[] { ',', ';' }))
                {
                    var trimmed = part.Trim();
                    if (trimmed.Length > 0)
                    {
                        result.Add(trimmed);
                    }
                }
            }
        }

        return result;
    }
}
