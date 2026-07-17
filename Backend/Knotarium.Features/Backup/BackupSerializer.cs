// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knotarium.Features.Backup;

/// <summary>
/// (De)serialization for the backup manifest and the per-aggregate data documents carried inside the
/// archive. Web-cased JSON with enum-as-string; the domain id value-objects already carry their own
/// <c>[JsonConverter]</c> attributes, so they round-trip as plain strings here.
/// </summary>
public static class BackupSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SerializeManifest(BackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, Options);
    }

    public static BackupManifest DeserializeManifest(string json)
    {
        var manifest = JsonSerializer.Deserialize<BackupManifest>(json, Options);
        if (manifest is null)
        {
            throw new InvalidOperationException("The backup manifest is empty or not valid backup.json.");
        }

        return manifest;
    }

    /// <summary>Serializes an aggregate list to its <c>data/&lt;name&gt;.json</c> document content.</summary>
    public static string Serialize<T>(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return JsonSerializer.Serialize(items, Options);
    }

    /// <summary>Deserializes a <c>data/&lt;name&gt;.json</c> document back into its aggregate list.</summary>
    public static IReadOnlyList<T> Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<List<T>>(json, Options) ?? new List<T>();
    }
}
