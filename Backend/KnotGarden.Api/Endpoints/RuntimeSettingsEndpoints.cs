using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using KnotGarden.Api.Services;
using KnotGarden.Api.Services.Auth;
using KnotGarden.Features.Settings;

namespace KnotGarden.Api;

/// <summary>
/// Host runtime/settings switches: the global arming state (design-time vs run-time) and the
/// default error workflow run whenever any workflow fails.
/// </summary>
public static class RuntimeSettingsEndpoints
{
    public static void MapRuntimeSettingsEndpoints(this WebApplication app)
    {
        // --- Runtime arming (global design-time vs run-time switch) ---

        app.MapGet("/api/runtime/arming", (RuntimeArmingState armingState) =>
            Results.Ok(new { armed = armingState.IsArmed }));

        app.MapPost("/api/runtime/arming", (SetArmingRequest request, RuntimeArmingState armingState) =>
        {
            armingState.SetArmed(request.Armed);
            return Results.Ok(new { armed = armingState.IsArmed });
        });

        // --- Global default error workflow (run whenever any workflow fails) ---

        app.MapGet("/api/settings/error-workflow", async (GlobalSettingsService settings, CancellationToken ct) =>
            Results.Ok(new { workflowId = await settings.GetDefaultErrorWorkflowIdAsync(ct) }));

        app.MapPut("/api/settings/error-workflow", async (SetErrorWorkflowRequest request, GlobalSettingsService settings, CancellationToken ct) =>
        {
            await settings.SetDefaultErrorWorkflowIdAsync(request.WorkflowId, ct);
            return Results.Ok(new { workflowId = await settings.GetDefaultErrorWorkflowIdAsync(ct) });
        });

        // --- Global file-access policy (path grants + free-space reserve enforced by the file nodes) ---
        // Reads are open to any authenticated user; mutations require the admin role (a no-op when auth is
        // disabled). This is the first role gate in the codebase; broader RBAC is still deferred.

        app.MapGet("/api/settings/file-access", async (FileAccessPolicyStore store, CancellationToken ct) =>
            Results.Ok(await store.GetDtoAsync(ct)));

        app.MapPut("/api/settings/file-access", async (FileAccessPolicyDto request, FileAccessPolicyStore store, AuthOptions auth, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (auth.RequireAdmin(user) is { } denied) return denied;
            await store.SetDtoAsync(request ?? FileAccessPolicyDto.Empty, ct);
            return Results.Ok(await store.GetDtoAsync(ct));
        });

        // --- Privileged capability switch (inline code / database), off by default ---

        app.MapGet("/api/settings/capabilities", async (CapabilityPolicyStore store, CancellationToken ct) =>
            Results.Ok(await store.GetDtoAsync(ct)));

        app.MapPut("/api/settings/capabilities", async (CapabilityPolicyDto request, CapabilityPolicyStore store, AuthOptions auth, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (auth.RequireAdmin(user) is { } denied) return denied;
            await store.SetDtoAsync(request ?? CapabilityPolicyDto.Empty, ct);
            return Results.Ok(await store.GetDtoAsync(ct));
        });
    }
}
