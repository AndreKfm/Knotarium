using System;
using System.Collections.Generic;
using Knotarium.Features.Portability;

namespace Knotarium.Features.Templates;

/// <summary>Raised when bytes are not a well-formed <c>.kgtpl</c> archive.</summary>
public sealed class TemplateArchiveException(string message) : InvalidOperationException(message);

/// <summary>
/// Reads and writes the <c>.kgtpl</c> zip over the shared <see cref="WorkflowArchiveCodec"/> (one
/// path-traversal guard and one set of zip-bomb limits for every format). Enforces the
/// <strong>closed entry-set</strong> for schema version 1: exactly <c>template.json</c> and
/// <c>workflow.json</c>, nothing else, no duplicates (anti-smuggling).
/// </summary>
public static class TemplateArchiveCodec
{
    /// <summary>Serializes a manifest + workflow document to a deterministic <c>.kgtpl</c> byte array.</summary>
    public static byte[] Write(TemplateArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(archive.Manifest);
        ArgumentNullException.ThrowIfNull(archive.WorkflowDocumentJson);

        var entries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TemplateFormat.ManifestEntryName] = TemplateSerializer.SerializeManifest(archive.Manifest),
            [TemplateFormat.WorkflowEntryName] = archive.WorkflowDocumentJson,
        };

        return WorkflowArchiveCodec.Write(entries);
    }

    /// <summary>Parses a <c>.kgtpl</c> byte array back into a <see cref="TemplateArchive"/>.</summary>
    /// <exception cref="TemplateArchiveException">Malformed, unexpected/duplicate entry, or unsupported schema version.</exception>
    public static TemplateArchive Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        IReadOnlyDictionary<string, string> entries;
        try
        {
            entries = WorkflowArchiveCodec.Read(bytes, WorkflowArchiveLimits.Default);
        }
        catch (WorkflowArchiveException ex)
        {
            throw new TemplateArchiveException(ex.Message);
        }

        // Closed entry-set: reject anything beyond the two known files (duplicates already rejected by the codec).
        foreach (var name in entries.Keys)
        {
            if (name != TemplateFormat.ManifestEntryName && name != TemplateFormat.WorkflowEntryName)
            {
                throw new TemplateArchiveException($"The template archive contains an unexpected entry '{name}'.");
            }
        }

        if (!entries.TryGetValue(TemplateFormat.ManifestEntryName, out var manifestJson))
        {
            throw new TemplateArchiveException($"The template archive is missing '{TemplateFormat.ManifestEntryName}'.");
        }

        if (!entries.TryGetValue(TemplateFormat.WorkflowEntryName, out var workflowJson))
        {
            throw new TemplateArchiveException($"The template archive is missing '{TemplateFormat.WorkflowEntryName}'.");
        }

        TemplateManifest manifest;
        try
        {
            manifest = TemplateSerializer.DeserializeManifest(manifestJson);
        }
        catch (InvalidOperationException ex)
        {
            throw new TemplateArchiveException(ex.Message);
        }

        if (manifest.SchemaVersion != TemplateFormat.SchemaVersion)
        {
            throw new TemplateArchiveException(
                $"Unsupported template schema version {manifest.SchemaVersion}; this engine supports {TemplateFormat.SchemaVersion}.");
        }

        return new TemplateArchive(manifest, workflowJson);
    }
}
