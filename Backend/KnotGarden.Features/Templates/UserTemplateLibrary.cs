using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Domain;
using KnotGarden.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnotGarden.Features.Templates;

/// <summary>
/// The user's saved-template library — the persisted counterpart to the read-only
/// <see cref="BuiltInTemplateGallery"/>. Saving packs the workflow exactly like an export (so the stored
/// <c>.kgtpl</c> is credential-portabilized and parameter-aware), then upserts a single row keyed by the
/// template id. Install/insert reuse <see cref="TemplateInstallService"/> / <see cref="TemplatePayloadService"/>
/// unchanged — this type only owns storage.
/// </summary>
public sealed class UserTemplateLibrary(
    AppDbContext dbContext,
    TemplateExportService exportService,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Packs <paramref name="request"/>'s workflow and saves it, upserting by template id (re-saving the same
    /// source workflow replaces its row rather than duplicating). Returns the saved manifest, or
    /// <see langword="null"/> when the workflow has no version to export.
    /// </summary>
    public async Task<GalleryTemplate?> SaveAsync(TemplateExportRequest request, CancellationToken cancellationToken = default)
    {
        var export = await exportService.ExportAsync(request, cancellationToken).ConfigureAwait(false);
        if (export is null)
        {
            return null;
        }

        var manifest = export.Manifest;
        await PersistWithRetryAsync(manifest, export.Bytes, cancellationToken).ConfigureAwait(false);
        return new GalleryTemplate(manifest.TemplateId, manifest);
    }

    /// <summary>
    /// Saves an already-packed <c>.kgtpl</c> (e.g. one a user uploaded on the Import tab) into the library,
    /// upserting by template id. The archive is verified (closed entry-set, checksum, content limits) before
    /// it is stored, so a tampered file is rejected rather than persisted.
    /// </summary>
    public async Task<GalleryTemplate> SaveArchiveAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var archive = TemplateArchiveCodec.Read(bytes);   // throws TemplateArchiveException on a malformed archive
        TemplateWorkflowReader.ReadAndVerify(archive);     // verify checksum + content-shape limits

        await PersistWithRetryAsync(archive.Manifest, bytes, cancellationToken).ConfigureAwait(false);
        return new GalleryTemplate(archive.Manifest.TemplateId, archive.Manifest);
    }

    private async Task PersistWithRetryAsync(TemplateManifest manifest, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            await UpsertAsync(manifest, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Lost the insert race against a concurrent save of the same TemplateId (the unique key). The row
            // now exists — drop our stale tracking and retry, which takes the update branch.
            foreach (var entry in dbContext.ChangeTracker.Entries<UserTemplate>().ToList())
            {
                entry.State = EntityState.Detached;
            }

            await UpsertAsync(manifest, bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpsertAsync(TemplateManifest manifest, byte[] archiveBytes, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Every persisted field is derived from the freshly packed manifest/archive here — the single writer,
        // so the denormalized columns can never drift from the packed source of truth.
        var existing = await dbContext.UserTemplates
            .FirstOrDefaultAsync(t => t.TemplateId == manifest.TemplateId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            dbContext.UserTemplates.Add(new UserTemplate
            {
                TemplateId = manifest.TemplateId,
                Name = manifest.Name,
                Author = manifest.Author,
                Description = manifest.Description,
                Category = manifest.Category,
                TemplateVersion = manifest.TemplateVersion,
                ManifestJson = TemplateSerializer.SerializeManifest(manifest),
                ArchiveBase64 = Convert.ToBase64String(archiveBytes),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.Name = manifest.Name;
            existing.Author = manifest.Author;
            existing.Description = manifest.Description;
            existing.Category = manifest.Category;
            existing.TemplateVersion = manifest.TemplateVersion;
            existing.ManifestJson = TemplateSerializer.SerializeManifest(manifest);
            existing.ArchiveBase64 = Convert.ToBase64String(archiveBytes);
            existing.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists every saved template (most-recently-saved first), reconstructing each manifest from the
    /// stored JSON — no archive is unpacked.</summary>
    public async Task<IReadOnlyList<GalleryTemplate>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.UserTemplates
            .AsNoTracking()
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new GalleryTemplate(row.TemplateId, TemplateSerializer.DeserializeManifest(row.ManifestJson)))
            .ToList();
    }

    /// <summary>Returns the packed <c>.kgtpl</c> bytes for one saved template, or <see langword="null"/> when absent.</summary>
    public async Task<byte[]?> GetArchiveBytesAsync(string templateId, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.UserTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TemplateId == templateId, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : Convert.FromBase64String(row.ArchiveBase64);
    }

    /// <summary>Removes a saved template. Returns <see langword="false"/> when no such template exists.</summary>
    public async Task<bool> RemoveAsync(string templateId, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.UserTemplates
            .FirstOrDefaultAsync(t => t.TemplateId == templateId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return false;
        }

        dbContext.UserTemplates.Remove(row);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
