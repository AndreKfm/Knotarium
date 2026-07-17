// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Knotarium.Api;

/// <summary>
/// Full-instance backup (.kgbak) admin endpoints: create a passphrase- or server-key-protected
/// snapshot, inspect one without writing, and perform the DESTRUCTIVE restore (guarded on a disarmed
/// runtime + explicit confirm, writing an auto pre-restore backup before replacing all state).
/// </summary>
public static class AdminBackupEndpoints
{
    public static void MapAdminBackupEndpoints(this WebApplication app)
    {
        // Produce a full-instance snapshot. Two protection modes: a passphrase (portable, restorable anywhere)
        // or this server's key (no passphrase, but restorable only on this host). The archive holds secrets in
        // re-encryptable form either way.
        app.MapPost("/api/admin/backup", async (
            CreateBackupRequest request,
            Knotarium.Features.Backup.BackupService backupService,
            Knotarium.Api.Services.Auth.AuthOptions auth,
            System.Security.Claims.ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            // A full-instance backup contains every secret in re-encryptable form — admin only.
            if (auth.RequireAdmin(user) is { } denied) return denied;
            if (request is null)
            {
                return Results.BadRequest(new { message = "A request body is required." });
            }

            var useServerKey = request.UseServerKey ?? false;
            if (!useServerKey && string.IsNullOrEmpty(request.Passphrase))
            {
                return Results.BadRequest(new { message = "A 'passphrase' is required unless 'useServerKey' is set." });
            }

            try
            {
                var includeRunHistory = request.IncludeRunHistory ?? false;
                var result = useServerKey
                    ? await backupService.CreateWithServerKeyAsync(includeRunHistory, cancellationToken)
                    : await backupService.CreateAsync(request.Passphrase!, includeRunHistory, cancellationToken);

                return Results.File(result.Bytes, "application/octet-stream", result.FileName);
            }
            catch (Knotarium.Features.Backup.BackupArchiveException ex)
            {
                // e.g. server-key mode requested but no credential key is configured on this host.
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        // Phase 2: preview a backup without writing anything — decrypt + parse the manifest only. Powers the
        // restore confirm flow (shows created-at, engine/format version, per-aggregate counts before committing).
        app.MapPost("/api/admin/backup/inspect", async (
            HttpRequest request,
            Knotarium.Features.Backup.BackupService backupService,
            Knotarium.Api.Services.Auth.AuthOptions auth,
            System.Security.Claims.ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            if (auth.RequireAdmin(user) is { } denied) return denied;
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { message = "Request must be multipart form-data with a 'backup' file and 'passphrase'." });
            }

            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("backup");
            // Passphrase is optional: a server-key backup auto-detects and needs none. The service returns a clear
            // 400 if a passphrase-protected backup arrives without one.
            var passphrase = form["passphrase"].ToString();
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { message = "No .kgbak file uploaded under 'backup'." });
            }

            byte[] bytes;
            using (var memory = new MemoryStream())
            {
                await file.CopyToAsync(memory, cancellationToken);
                bytes = memory.ToArray();
            }

            try
            {
                var manifest = await backupService.InspectAsync(bytes, passphrase, cancellationToken);
                return Results.Ok(manifest);
            }
            catch (Knotarium.Features.Backup.BackupIncompatibleException ex)
            {
                return Results.Json(new { message = ex.Message, manifest = ex.Manifest }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (Knotarium.Features.Backup.BackupArchiveException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        // Phase 2: DESTRUCTIVE full-instance restore. Guarded: runtime must be disarmed (412), confirm must be true
        // (422). Writes an auto pre-restore backup first, then replaces all state in one transaction.
        app.MapPost("/api/admin/restore", async (
            HttpRequest request,
            Knotarium.Features.Backup.BackupService backupService,
            Knotarium.Api.Services.Auth.AuthOptions auth,
            System.Security.Claims.ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            if (auth.RequireAdmin(user) is { } denied) return denied;
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { message = "Request must be multipart form-data with a 'backup' file, 'passphrase', and 'confirm'." });
            }

            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("backup");
            // Passphrase optional — server-key backups need none (auto-detected from the archive header).
            var passphrase = form["passphrase"].ToString();
            var confirm = string.Equals(form["confirm"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { message = "No .kgbak file uploaded under 'backup'." });
            }

            byte[] bytes;
            using (var memory = new MemoryStream())
            {
                await file.CopyToAsync(memory, cancellationToken);
                bytes = memory.ToArray();
            }

            try
            {
                var report = await backupService.RestoreAsync(bytes, passphrase, confirm, cancellationToken);
                return Results.Ok(new
                {
                    restored = report.Restored,
                    manifest = report.Manifest,
                    preRestoreBackupPath = report.PreRestoreBackupPath,
                });
            }
            catch (Knotarium.Features.Backup.BackupRestoreBlockedException ex)
            {
                var statusCode = ex.Reason == Knotarium.Features.Backup.RestoreBlockReason.RuntimeArmed
                    ? StatusCodes.Status412PreconditionFailed
                    : StatusCodes.Status422UnprocessableEntity;
                return Results.Json(new { message = ex.Message, reason = ex.Reason.ToString() }, statusCode: statusCode);
            }
            catch (Knotarium.Features.Backup.BackupIncompatibleException ex)
            {
                return Results.Json(new { message = ex.Message, manifest = ex.Manifest }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (Knotarium.Features.Backup.BackupArchiveException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });
    }
}
