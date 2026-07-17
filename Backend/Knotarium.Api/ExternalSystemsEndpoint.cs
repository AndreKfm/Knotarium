// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;

namespace Knotarium.Api;

/// <summary>Body for renaming the administered system.</summary>
public sealed record RenameSystemRequest(string Name);

/// <summary>Body for flipping a provider-declared system-level option.</summary>
public sealed record SetOptionRequest(string Key, bool Value);

/// <summary>
/// Generic admin endpoints for an external-signal provider that opts into administration by registering
/// an <see cref="IExternalSignalAdmin"/>. The host stays vendor-neutral — it shuttles the generic DTOs
/// and never names any specific provider. When no provider supports administration the surface 404s, so
/// the UI can hide itself. Secrets are write-only: they go in on save, never come back.
/// </summary>
public static class ExternalSystemsEndpoint
{
    // Sync / test connection reach a live box; cap them so a hung system can't stall the request thread.
    private static readonly TimeSpan LiveOpTimeout = TimeSpan.FromSeconds(20);

    public static void MapExternalSystemsEndpoint(this WebApplication app)
    {
        // Vendor-supplied UI descriptor (branding + nouns + capability flags). Drives whether the UI
        // shows the section at all.
        app.MapGet("/api/external-systems/descriptor", (IServiceProvider sp) =>
        {
            var admin = sp.GetService<IExternalSignalAdmin>();
            return admin is null
                ? Results.NotFound(new { message = "No external-signal provider supports administration." })
                : Results.Ok(admin.Describe());
        });

        app.MapGet("/api/external-systems", async (IServiceProvider sp, CancellationToken ct) =>
        {
            var admin = sp.GetService<IExternalSignalAdmin>();
            if (admin is null) return NoAdmin();
            return Results.Ok(await admin.GetSystemAsync(ct));
        });

        // Clear the live diagnostics feed (recent auto-filtered signals + counters). Observed-only data.
        app.MapDelete("/api/external-systems/diagnostics", async (IServiceProvider sp, CancellationToken ct) =>
        {
            var admin = sp.GetService<IExternalSignalAdmin>();
            if (admin is null) return NoAdmin();
            return Results.Ok(await admin.ClearDiagnosticsAsync(ct));
        });

        app.MapPut("/api/external-systems", async (RenameSystemRequest request, IServiceProvider sp, CancellationToken ct) =>
        {
            var admin = sp.GetService<IExternalSignalAdmin>();
            if (admin is null) return NoAdmin();
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { message = "Name is required." });
            return Results.Ok(await admin.RenameSystemAsync(request.Name.Trim(), ct));
        });

        // Flip a provider-declared system option (e.g. self-echo suppression). Applied live by the provider.
        app.MapPut("/api/external-systems/options", async (
            SetOptionRequest request, IServiceProvider sp, CancellationToken ct) =>
        {
            var admin = sp.GetService<IExternalSignalAdmin>();
            if (admin is null) return NoAdmin();
            if (string.IsNullOrWhiteSpace(request.Key))
                return Results.BadRequest(new { message = "Option key is required." });
            try
            {
                return Results.Ok(await admin.SetOptionAsync(request.Key.Trim(), request.Value, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapPost("/api/external-systems/targets", async (
            ExternalTargetEdit edit, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
        {
            var admin = sp.GetService<IExternalSignalAdmin>();
            if (admin is null) return NoAdmin();
            if (string.IsNullOrWhiteSpace(edit.Name) || string.IsNullOrWhiteSpace(edit.Host))
                return Results.BadRequest(new { message = "Name and host are required." });
            try
            {
                return Results.Ok(await admin.UpsertTargetAsync(edit, ct));
            }
            catch (InvalidOperationException ex)
            {
                lf.CreateLogger("ExternalSystems").LogInformation(ex, "Upsert target rejected.");
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapDelete("/api/external-systems/targets/{targetId}", async (
            string targetId, IServiceProvider sp, CancellationToken ct) =>
        {
            var admin = sp.GetService<IExternalSignalAdmin>();
            if (admin is null) return NoAdmin();
            try
            {
                await admin.DeleteTargetAsync(targetId, ct);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        // Live discovery: pull the catalog from the box and persist it. Always-Ok envelope so a UI Sync
        // button surfaces the failure inline rather than as a hard error.
        app.MapPost("/api/external-systems/targets/{targetId}/sync", async (
            string targetId, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
        {
            var admin = sp.GetService<IExternalSignalAdmin>();
            if (admin is null) return NoAdmin();
            using var timeout = LinkedTimeout(ct);
            try
            {
                return Results.Ok(await admin.SyncTargetAsync(targetId, timeout.Token));
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                lf.CreateLogger("ExternalSystems").LogInformation(ex, "Sync failed for target {Target}.", targetId);
                return Results.BadRequest(new { message = SyncErrorText(ex) });
            }
        });

        app.MapPost("/api/external-systems/targets/test", async (
            ExternalTargetEdit candidate, IServiceProvider sp, CancellationToken ct) =>
        {
            var admin = sp.GetService<IExternalSignalAdmin>();
            if (admin is null) return NoAdmin();
            using var timeout = LinkedTimeout(ct);
            try
            {
                return Results.Ok(await admin.TestConnectionAsync(candidate, timeout.Token));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Ok(new TargetStatus(candidate.Id ?? string.Empty, TargetConnectivity.Faulted,
                    LastError: "The system did not respond in time."));
            }
        });
    }

    private static IResult NoAdmin()
        => Results.NotFound(new { message = "No external-signal provider supports administration." });

    private static CancellationTokenSource LinkedTimeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LiveOpTimeout);
        return cts;
    }

    private static string SyncErrorText(Exception ex)
        => ex is OperationCanceledException ? "The system did not respond in time." : ex.Message;
}
