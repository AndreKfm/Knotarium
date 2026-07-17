// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Knotarium.Features.Bundles;

namespace Knotarium.Api;

/// <summary>
/// Integration-library bundle (.kgbundle) export/install. Export resolves + locks the manifest and
/// zips it; install verifies, rebinds credential slots, and reports a package-verification gate —
/// distinguishing a same-version/different-hash conflict (409) from a gate rejection (422).
/// </summary>
public static class BundleEndpoints
{
    public static void MapBundleEndpoints(this WebApplication app)
    {
        app.MapPost("/api/bundles/export", async (
            BundleManifest manifest,
            BundleExportService exportService,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.BundleId))
            {
                return Results.BadRequest(new { message = "A bundle manifest with a bundleId is required." });
            }

            var trustedKeys = configuration
                .GetSection("Security:PackageSigning:TrustedPublicKeys")
                .Get<string[]>() ?? Array.Empty<string>();

            try
            {
                var bytes = await exportService.ExportAsync(
                    new BundleExportInput(manifest, trustedKeys, BundleExportService.DefaultResolverVersion),
                    cancellationToken);

                var version = string.IsNullOrWhiteSpace(manifest.BundleVersion) ? "0.0.0" : manifest.BundleVersion;
                return Results.File(bytes, "application/zip", $"{manifest.BundleId}-{version}.kgbundle");
            }
            catch (BundlePackageNotFoundException ex)
            {
                return Results.BadRequest(new { message = ex.Message, packageId = ex.PackageId, constraint = ex.Constraint });
            }
            catch (BundleWorkflowNotFoundException ex)
            {
                return Results.BadRequest(new { message = ex.Message, workflowKey = ex.WorkflowKey });
            }
        });

        app.MapPost("/api/bundles/install", async (
            HttpRequest request,
            BundleInstallService installService,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { message = "Request must be multipart form-data with a 'bundle' file." });
            }

            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("bundle");
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { message = "No .kgbundle file uploaded under 'bundle'." });
            }

            var allowProvisional = string.Equals(form["allowProvisional"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            var acknowledgePrivileged = string.Equals(form["acknowledgePrivileged"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            var trustedKeys = configuration
                .GetSection("Security:PackageSigning:TrustedPublicKeys")
                .Get<string[]>() ?? Array.Empty<string>();

            // Optional slot→credentialId map as a JSON object, e.g. {"smtp":"cred-123"}.
            IReadOnlyDictionary<string, string>? credentialBindings = null;
            var bindingsJson = form["credentialBindings"].ToString();
            if (!string.IsNullOrWhiteSpace(bindingsJson))
            {
                try
                {
                    credentialBindings = JsonSerializer.Deserialize<Dictionary<string, string>>(bindingsJson);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { message = "'credentialBindings' must be a JSON object of slot→credentialId." });
                }
            }

            byte[] bytes;
            using (var memory = new MemoryStream())
            {
                await file.CopyToAsync(memory, cancellationToken);
                bytes = memory.ToArray();
            }

            try
            {
                var result = await installService.InstallAsync(
                    bytes, trustedKeys, allowProvisional, credentialBindings, acknowledgePrivileged, cancellationToken);

                var payload = new
                {
                    installed = result.Installed,
                    installedPackages = result.InstalledPackages,
                    skippedPackages = result.SkippedPackages,
                    importedWorkflows = result.ImportedWorkflows,
                    requiredCredentialSlots = result.RequiredCredentialSlots,
                    reboundCredentialSlots = result.ReboundCredentialSlots,
                    unboundCredentialSlots = result.UnboundCredentialSlots,
                    conflictingPackages = result.ConflictingPackages,
                    verification = result.Verification.Packages,
                    blocking = result.Verification.Blocking,
                    privilegedNodes = result.PrivilegedNodes,
                    privilegedAcknowledgementRequired = result.PrivilegedAcknowledgementRequired,
                };

                if (result.Installed)
                {
                    return Results.Ok(payload);
                }

                // A same-version-different-hash conflict is a 409 (the registry state disagrees with the bundle);
                // a verification gate failure is a 422 (the bundle itself was understood and rejected).
                return result.ConflictingPackages.Count > 0
                    ? Results.Conflict(payload)
                    : Results.UnprocessableEntity(payload);
            }
            catch (BundleArchiveException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (BundleWorkflowNotFoundException ex)
            {
                return Results.BadRequest(new { message = ex.Message, workflowKey = ex.WorkflowKey });
            }
        });
    }
}
