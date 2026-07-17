// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Knotarium.Api.Services.Auth;
using Knotarium.Features.NodeEditor;

namespace Knotarium.Api;

/// <summary>
/// Host-level utility endpoints: the node-editor sandbox test (which marks a package version as
/// having passed the mandatory gate) and the build-identity probe used by the dashboard to tell a
/// running instance apart from a stale one.
/// </summary>
public static class HostEndpoints
{
    public static void MapHostEndpoints(this WebApplication app)
    {
        app.MapPost("/api/node-editor/test", async (NodeEditorTestRequest request, INodeEditorSandboxService sandbox, INodeEditorSessionGate gate, AuthOptions auth, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            // Authoring/testing a compiled node compiles + runs C# in-process — an admin-only action,
            // consistent with node-package install. The sandbox also enforces the code-execution
            // capability gate; this is the outer, defence-in-depth check (a no-op when auth is disabled).
            if (auth.RequireAdmin(user) is { } denied) return denied;

            var result = await sandbox.RunTestsAsync(request, cancellationToken);

            if (result.Success)
            {
                var version = "1.0.0";
                if (!string.IsNullOrWhiteSpace(request.ManifestYaml))
                {
                    var lines = request.ManifestYaml.Split('\n');
                    var versionLine = lines.FirstOrDefault(line => line.TrimStart().StartsWith("version:", StringComparison.OrdinalIgnoreCase));
                    if (versionLine != null)
                    {
                        var rawVersion = versionLine.Split(':', 2).LastOrDefault()?.Trim();
                        if (!string.IsNullOrWhiteSpace(rawVersion))
                        {
                            version = rawVersion;
                        }
                    }
                }

                gate.MarkPassed(request.PackageId, version);
            }

            return Results.Ok(result);
        });

        // Build identity for the dashboard, so a running instance can be told apart from a stale one. The
        // version comes from the assembly (csproj <Version>); the build time is the assembly file's timestamp,
        // which changes on every rebuild even when the version string doesn't — the reliable "is this fresh?" signal.
        app.MapGet("/api/version", () =>
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(Program).Assembly;
            var infoAttr = (System.Reflection.AssemblyInformationalVersionAttribute?)System.Attribute.GetCustomAttribute(
                asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            var version = infoAttr?.InformationalVersion
                          ?? asm.GetName().Version?.ToString()
                          ?? "0.0.0";
            var plus = version.IndexOf('+'); // strip the +<gitsha> SourceLink appends
            if (plus >= 0) version = version[..plus];

            DateTimeOffset? buildTimeUtc = null;
            try
            {
                var loc = asm.Location;
                if (!string.IsNullOrEmpty(loc) && System.IO.File.Exists(loc))
                    buildTimeUtc = System.IO.File.GetLastWriteTimeUtc(loc);
            }
            catch { /* location unavailable (e.g. single-file) — omit build time rather than fail */ }

            return Results.Ok(new { version, buildTimeUtc });
        });
    }
}
