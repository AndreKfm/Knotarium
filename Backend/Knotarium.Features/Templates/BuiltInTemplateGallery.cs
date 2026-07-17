// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Knotarium.Features.Execution;
using Knotarium.Features.Portability;

namespace Knotarium.Features.Templates;

/// <summary>
/// Serves the starter templates shipped with the app. The catalog is stored as <em>reviewable, unzipped
/// sources</em> — <c>Templates/Sources/&lt;name&gt;/{template.json, workflow.json}</c> — never as committed
/// binaries. The gallery packs a deterministic <c>.kgtpl</c> on demand, recomputing the workflow checksum
/// from the source so an authored template is always internally consistent.
/// </summary>
public sealed class BuiltInTemplateGallery(string sourcesDirectory)
{
    /// <summary>The default location, resolved next to the running assembly (copied there as Content).</summary>
    public static string DefaultSourcesDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Templates", "Sources");

    /// <summary>Lists every built-in template's manifest (parse only; nothing is installed).</summary>
    public Task<IReadOnlyList<GalleryTemplate>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourcesDirectory))
        {
            return Task.FromResult<IReadOnlyList<GalleryTemplate>>([]);
        }

        var result = new List<GalleryTemplate>();
        foreach (var directory in Directory.EnumerateDirectories(sourcesDirectory).OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archive = TryBuildArchive(directory);
            if (archive is not null)
            {
                result.Add(new GalleryTemplate(archive.Manifest.TemplateId, archive.Manifest));
            }
        }

        return Task.FromResult<IReadOnlyList<GalleryTemplate>>(result);
    }

    /// <summary>Returns the manifest for one built-in template, or <see langword="null"/> when not found.</summary>
    public async Task<GalleryTemplate?> GetAsync(string templateId, CancellationToken cancellationToken = default)
    {
        var all = await ListAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(item => item.TemplateId == templateId);
    }

    /// <summary>Packs the named built-in template into <c>.kgtpl</c> bytes, or <see langword="null"/> when not found.</summary>
    public Task<byte[]?> GetArchiveBytesAsync(string templateId, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourcesDirectory))
        {
            return Task.FromResult<byte[]?>(null);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourcesDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archive = TryBuildArchive(directory);
            if (archive is not null && archive.Manifest.TemplateId == templateId)
            {
                return Task.FromResult<byte[]?>(TemplateArchiveCodec.Write(archive));
            }
        }

        return Task.FromResult<byte[]?>(null);
    }

    // Builds a consistent archive from a source folder, recomputing the workflow checksum and re-canonicalizing
    // the workflow document so hand-authored sources never carry a stale checksum or non-canonical layout.
    private static TemplateArchive? TryBuildArchive(string directory)
    {
        var manifestPath = Path.Combine(directory, TemplateFormat.ManifestEntryName);
        var workflowPath = Path.Combine(directory, TemplateFormat.WorkflowEntryName);
        if (!File.Exists(manifestPath) || !File.Exists(workflowPath))
        {
            return null;
        }

        var manifest = TemplateSerializer.DeserializeManifest(File.ReadAllText(manifestPath));
        var document = WorkflowVersionSerializer.Deserialize(File.ReadAllText(workflowPath));
        var canonicalJson = WorkflowVersionSerializer.Serialize(document);
        var checksum = WorkflowVersionSerializer.ComputeChecksum(document.Content);

        var consistent = manifest with
        {
            SchemaVersion = TemplateFormat.SchemaVersion,
            WorkflowChecksum = checksum,
        };
        return new TemplateArchive(consistent, canonicalJson);
    }
}
