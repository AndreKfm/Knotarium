// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Polling;

/// <summary>
/// Transport-agnostic change detection over a response body. Handles Hash, JsonCursor and Always.
/// Etag / LastModified are transport-level and handled by the source itself.
/// </summary>
public static class BodyChangeDetector
{
    public static PollResult Detect(PollChangeDetection strategy, string body, string? cursor, string? jsonPath)
    {
        switch (strategy)
        {
            case PollChangeDetection.Always:
                return new PollResult(HasNew: true, Payload: body, NewCursor: cursor);

            case PollChangeDetection.Hash:
            {
                var hash = ComputeHash(body);
                var hasNew = !string.Equals(hash, cursor, StringComparison.Ordinal);
                return new PollResult(hasNew, Payload: hasNew ? body : null, NewCursor: hash);
            }

            case PollChangeDetection.JsonCursor:
            {
                var value = ExtractJsonValue(body, jsonPath);
                if (value is null)
                {
                    // Path missing: treat as no change so a malformed/empty response never floods runs.
                    return new PollResult(HasNew: false, Payload: null, NewCursor: cursor);
                }

                var hasNew = IsAdvanced(value, cursor);
                return new PollResult(hasNew, Payload: hasNew ? body : null, NewCursor: hasNew ? value : cursor);
            }

            case PollChangeDetection.Etag:
            case PollChangeDetection.LastModified:
                throw new InvalidOperationException(
                    $"{strategy} is transport-level and must be handled by the source, not BodyChangeDetector.");

            default:
                throw new ArgumentOutOfRangeException(nameof(strategy), strategy,
                    "BodyChangeDetector only handles Hash, JsonCursor and Always.");
        }
    }

    private static string ComputeHash(string body)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(bytes);
    }

    private static string? ExtractJsonValue(string body, string? jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var element = doc.RootElement;
            foreach (var segment in jsonPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !element.TryGetProperty(segment, out var next))
                {
                    return null;
                }

                element = next;
            }

            return element.ValueKind switch
            {
                JsonValueKind.Null => null,           // JSON null is "no value" — treat as missing path
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
                _ => element.GetRawText()
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsAdvanced(string value, string? cursor)
    {
        if (cursor is null)
        {
            return true;
        }

        if (string.Equals(value, cursor, StringComparison.Ordinal))
        {
            return false;
        }

        // Numeric cursors must strictly increase; non-numeric cursors are "new" on any difference.
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var newNum) &&
            double.TryParse(cursor, NumberStyles.Any, CultureInfo.InvariantCulture, out var oldNum))
        {
            return newNum > oldNum;
        }

        return true;
    }
}
