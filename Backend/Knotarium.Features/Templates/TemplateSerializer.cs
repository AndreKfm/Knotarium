// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knotarium.Features.Templates;

/// <summary>
/// Lossless (de)serialization of <c>template.json</c>. Web-cased, enum-as-string, human-readable —
/// the round-trippable on-disk shape of a <see cref="TemplateManifest"/>.
/// </summary>
public static class TemplateSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SerializeManifest(TemplateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, Options);
    }

    public static TemplateManifest DeserializeManifest(string json)
    {
        TemplateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<TemplateManifest>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"template.json is not valid JSON: {ex.Message}");
        }

        if (manifest is null)
        {
            throw new InvalidOperationException("template.json is empty or not a valid template manifest.");
        }

        return manifest;
    }
}
