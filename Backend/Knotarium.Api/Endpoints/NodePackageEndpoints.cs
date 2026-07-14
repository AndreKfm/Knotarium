using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Knotarium.Api.Services.Auth;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.NodeEditor;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Security;

namespace Knotarium.Api;

/// <summary>
/// Node-package registry endpoints: read the installed manifest set, plus the two write paths that
/// share <see cref="InstallNodePackageAsync"/> — <c>install</c> (verifies a trusted signature) and
/// <c>publish</c> (enforces the sandbox-test gate and signs with the host key).
/// </summary>
public static class NodePackageEndpoints
{
    public static void MapNodePackageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/node-packages", async (DbNodePackageManifestProvider manifestProvider, CancellationToken cancellationToken) =>
            Results.Ok(await manifestProvider.GetNodePackagesAsync(cancellationToken)));

        // install/publish add a code-execution supply-chain surface (compiled custom nodes run in-process),
        // so they are admin-gated like the other privileged mutations — a no-op when auth is disabled.
        app.MapPost("/api/node-packages/install", async (HttpRequest request, AppDbContext db, AuthOptions auth, ClaimsPrincipal user) =>
        {
            if (auth.RequireAdmin(user) is { } denied) return denied;
            return await InstallNodePackageAsync(request, db, request.HttpContext.RequestServices.GetRequiredService<IConfiguration>(), enforcePublishGate: false, gate: null);
        });

        app.MapPost("/api/node-packages/publish", async (HttpRequest request, AppDbContext db, INodeEditorSessionGate gate, AuthOptions auth, ClaimsPrincipal user) =>
        {
            if (auth.RequireAdmin(user) is { } denied) return denied;
            return await InstallNodePackageAsync(request, db, request.HttpContext.RequestServices.GetRequiredService<IConfiguration>(), enforcePublishGate: true, gate);
        });
    }

    private static async Task<IResult> InstallNodePackageAsync(HttpRequest request, AppDbContext db, IConfiguration configuration, bool enforcePublishGate, INodeEditorSessionGate? gate)
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { message = "Request must be a multipart form-data" });
        }

        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("package");
        var signature = form["signature"].ToString();

        if (file == null || file.Length == 0)
        {
            return Results.BadRequest(new { message = "No package file uploaded" });
        }

        var displayName = form["displayName"].ToString();
        var category = form["category"].ToString();
        var packageIdStr = form["packageId"].ToString();
        var versionStr = form["version"].ToString();
        var manifestJson = form["manifestJson"].ToString();
        var sourceCode = form["sourceCode"].ToString();

        if (string.IsNullOrWhiteSpace(packageIdStr))
        {
            packageIdStr = Path.GetFileNameWithoutExtension(file.FileName).ToLower();
        }
        if (string.IsNullOrWhiteSpace(versionStr))
        {
            versionStr = "1.0.0";
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = packageIdStr;
        }
        if (string.IsNullOrWhiteSpace(category))
        {
            category = "Utility";
        }
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            manifestJson = "{}";
        }

        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            sourceCode = "ZIP Extracted Binary";
        }

        var capabilities = new List<string> { "logging" };
        var signingPayload = new PackageSigningPayload(
            packageIdStr,
            versionStr,
            displayName,
            category,
            manifestJson,
            sourceCode,
            capabilities);

        if (enforcePublishGate)
        {
            if (gate == null || !gate.HasPassingResult(packageIdStr, versionStr))
            {
                return Results.BadRequest(new
                {
                    message = "Mandatory Gate Violated: You must successfully run and pass all sandbox tests before publishing this package version."
                });
            }

            var hostPrivateKeyBase64 = configuration["Security:PackageSigning:HostPrivateKeyBase64"];
            if (string.IsNullOrWhiteSpace(hostPrivateKeyBase64))
            {
                return Results.Problem(
                    title: "Host package signing is not configured",
                    detail: "Security:PackageSigning:HostPrivateKeyBase64 must be configured for publish operations.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            byte[] hostPrivateKey;
            try
            {
                hostPrivateKey = Convert.FromBase64String(hostPrivateKeyBase64);
            }
            catch (FormatException)
            {
                return Results.Problem(
                    title: "Host package signing key is invalid",
                    detail: "Security:PackageSigning:HostPrivateKeyBase64 must be valid base64.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            if (hostPrivateKey.Length != 32)
            {
                return Results.Problem(
                    title: "Host package signing key has invalid length",
                    detail: "Security:PackageSigning:HostPrivateKeyBase64 must decode to 32 bytes.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            signature = PackageSigner.Sign(signingPayload, hostPrivateKey);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(signature))
            {
                return Results.BadRequest(new { message = "Cryptographic signature is required" });
            }

            var trustedPublicKeys = configuration
                .GetSection("Security:PackageSigning:TrustedPublicKeys")
                .Get<string[]>() ?? Array.Empty<string>();

            if (!PackageSigner.Verify(signingPayload, signature, trustedPublicKeys))
            {
                return Results.BadRequest(new { message = "Cryptographic signature verification failed" });
            }
        }

        var packageId = NodePackageId.Create(packageIdStr);

        var existingPackage = await db.NodePackages
            .Include(p => p.Versions)
            .FirstOrDefaultAsync(p => p.Id == packageId);

        if (existingPackage == null)
        {
            existingPackage = new NodePackage
            {
                Id = packageId,
                DisplayName = displayName,
                Category = category
            };
            db.NodePackages.Add(existingPackage);
        }

        var packageVersion = new NodePackageVersion
        {
            Id = NodePackageVersionId.New(),
            NodePackageId = packageId,
            Version = versionStr,
            ManifestJson = manifestJson,
            Source = sourceCode,
            Signature = signature,
            Capabilities = capabilities,
            CreatedAt = DateTimeOffset.UtcNow
        };

        existingPackage.Versions.Add(packageVersion);
        await db.SaveChangesAsync();

        return Results.Ok(new { message = "Package installed successfully", packageId = packageId.Value, version = versionStr });
    }
}
