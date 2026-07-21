// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Knotarium.Api.Services;
using Knotarium.Api.Services.Auth;
using Knotarium.Core.Domain;
using Knotarium.Features.Settings;

namespace Knotarium.Api;

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

        app.MapPost("/api/runtime/arming", async (SetArmingRequest request, RuntimeArmingState armingState, GlobalSettingsService settings, AuthOptions auth, ClaimsPrincipal user, CancellationToken ct) =>
        {
            // Arming is the master switch that enables anonymous webhook/external triggers, so flipping it
            // is an admin action (a no-op gate when auth is disabled).
            if (auth.RequireAdmin(user) is { } denied) return denied;
            armingState.SetArmed(request.Armed);
            // Persist the explicit operator choice so it survives restarts (restored in Program.cs after
            // schema migration). Only this endpoint writes the key: transient safety disarms (disk-space
            // guard) stay in-memory so a low-disk blip can't permanently disarm the instance.
            await settings.SetAsync(AppSettingKeys.RuntimeArmed, request.Armed ? "true" : "false", ct);
            return Results.Ok(new { armed = armingState.IsArmed });
        });

        // --- Read-only run-level execution runtime (active concurrency limit, queue, journal batching) ---
        // Operator visibility for Execution:* configuration knobs; changing them requires a restart.

        app.MapGet("/api/runtime/execution", (
            Knotarium.Features.Execution.ExecutionOptions options,
            Knotarium.Features.Execution.WorkflowExecutionQueue queue,
            Knotarium.Features.Execution.ExecutionRuntimeMonitor monitor) =>
            Results.Ok(new
            {
                maxConcurrentRuns = options.MaxConcurrentRuns,
                inFlightRuns = monitor.InFlightRuns,
                queueDepth = queue.Depth,
                maxQueueDepth = queue.MaxDepth,
                rejectedStarts = monitor.RejectedStarts,
                journalBatching = new
                {
                    enabled = options.JournalBatchingEnabled,
                    maxBatchSize = options.JournalBatchMaxSize,
                    maxDelayMilliseconds = options.JournalBatchMaxDelayMilliseconds
                }
            }));

        // --- Global default error workflow (run whenever any workflow fails) ---

        app.MapGet("/api/settings/error-workflow", async (GlobalSettingsService settings, CancellationToken ct) =>
            Results.Ok(new { workflowId = await settings.GetDefaultErrorWorkflowIdAsync(ct) }));

        app.MapPut("/api/settings/error-workflow", async (SetErrorWorkflowRequest request, GlobalSettingsService settings, AuthOptions auth, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (auth.RequireAdmin(user) is { } denied) return denied;
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

        // --- Sandbox for user-authored node code (execution mode, worker limits, credential proxy) ---
        // Mode changes apply immediately (the switchable runner routes per call); WorkerCount only
        // sizes a pool created after the change; per-worker limits apply to newly spawned workers.

        app.MapGet("/api/settings/sandbox", async (Knotarium.Features.Nodes.Sandbox.SandboxSettingsStore store, CancellationToken ct) =>
            Results.Ok(await store.GetDtoAsync(ct)));

        app.MapPut("/api/settings/sandbox", async (Knotarium.Features.Nodes.Sandbox.SandboxSettingsDto request, Knotarium.Features.Nodes.Sandbox.SandboxSettingsStore store, AuthOptions auth, ClaimsPrincipal user, CancellationToken ct) =>
        {
            // Where (and how confined) arbitrary C# runs is a security decision — admin only.
            if (auth.RequireAdmin(user) is { } denied) return denied;
            return Results.Ok(await store.SetDtoAsync(request, ct));
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

        // --- Data-retention policy (bounds DB growth: run history + logs, version history, audit log) ---
        // The JournalRetentionWorker re-reads this on every sweep, so an edit applies without a restart.
        // Reads are open to any authenticated user; mutations require the admin role (a no-op without auth).

        app.MapGet("/api/settings/retention", async (RetentionPolicyStore store, CancellationToken ct) =>
            Results.Ok(await store.GetDtoAsync(ct)));

        app.MapPut("/api/settings/retention", async (RetentionPolicyDto request, RetentionPolicyStore store, AuthOptions auth, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (auth.RequireAdmin(user) is { } denied) return denied;
            if (request is null) return Results.BadRequest(new { error = "A retention policy body is required." });
            return Results.Ok(await store.SetDtoAsync(request, ct));
        });
    }
}
