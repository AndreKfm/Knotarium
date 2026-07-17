// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Knotarium.Core.Domain;
using Knotarium.Features.Ai;
using Knotarium.Features.Ai.Providers;

namespace Knotarium.Api.Services.Ai;

/// <summary>
/// Request to start an AI workflow generation job. When <see cref="Workflow"/> is supplied, the job
/// REFINES that existing workflow per the intent instead of generating a new one from scratch.
/// </summary>
public sealed record GenerateWorkflowRequest(string Intent, WorkflowDefinition? Workflow = null);

/// <summary>The active AI provider config for the Settings UI. Never includes the API key itself.</summary>
public sealed record AiProviderConfigResponse(
    string? Vendor,
    string? Model,
    string? CredentialRef,
    string? BaseUrl,
    string? ApiVersion,
    int? MaxTokens,
    string[] AvailableVendors);

public sealed record SetAiProviderConfigRequest(
    string? Vendor,
    string? Model,
    string? CredentialRef,
    string? BaseUrl,
    string? ApiVersion,
    int? MaxTokens);

/// <summary>Outcome of a provider connection test. <see cref="Ok"/> false carries a human-readable reason.</summary>
public sealed record AiProviderTestResponse(bool Ok, string Message, int? LatencyMs, string? Model);

/// <summary>Best-effort live model ids for the supplied provider config (empty = fall back to curated list).</summary>
public sealed record AiProviderModelsResponse(System.Collections.Generic.IReadOnlyList<string> Models);

/// <summary>
/// Poll response for a generation job. On success <see cref="Workflow"/> is the generated definition
/// (serialized through the same converters as every other workflow, so the editor consumes it directly)
/// and <see cref="OpenSlots"/> lists the credential slots the user must bind before it can run.
/// </summary>
public sealed record AiGenerationJobResponse(
    string JobId,
    string Status,
    WorkflowDefinition? Workflow,
    IReadOnlyList<string> OpenSlots,
    IReadOnlyList<string> Diagnostics,
    int Attempts,
    string? Error);

public static class AiGenerationEndpoint
{
    public static void MapAiGenerationEndpoints(this WebApplication app)
    {
        // Start a generation job. Returns immediately with a job id; the hosted worker runs the
        // generate→compile→repair loop in the background and the client polls the GET endpoint.
        app.MapPost("/api/ai/generate", (
            GenerateWorkflowRequest request,
            AiGenerationJobStore store,
            AiGenerationQueue queue,
            AiGenerationOptions options) =>
        {
            var intent = request.Intent?.Trim() ?? string.Empty;
            if (intent.Length == 0)
            {
                return Results.BadRequest(new { error = "Intent is required." });
            }
            if (intent.Length > options.MaxIntentLength)
            {
                return Results.BadRequest(new { error = $"Intent exceeds the maximum of {options.MaxIntentLength} characters." });
            }

            var job = store.Create(intent, request.Workflow);
            queue.Enqueue(job.Id);
            return Results.Ok(new { jobId = job.Id });
        });

        // Poll a generation job's status / result.
        app.MapGet("/api/ai/generate/{jobId}", (string jobId, AiGenerationJobStore store) =>
        {
            var job = store.Get(jobId);
            if (job is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new AiGenerationJobResponse(
                job.Id,
                job.Status.ToString(),
                job.Workflow,
                job.OpenSlots,
                job.Diagnostics,
                job.Attempts,
                job.Error));
        });

        // Read the active AI provider config (vendor/model/credential ref — never the key itself).
        app.MapGet("/api/settings/ai-provider", async (IAiProviderConfigStore store, CancellationToken ct) =>
        {
            var c = await store.GetAsync(ct);
            return Results.Ok(new AiProviderConfigResponse(
                c?.Vendor, c?.Model, c?.CredentialRef, c?.BaseUrl, c?.ApiVersion, c?.MaxTokens, LlmVendors.All));
        });

        // Set the active AI provider config.
        app.MapPut("/api/settings/ai-provider", async (SetAiProviderConfigRequest request, IAiProviderConfigStore store, CancellationToken ct) =>
        {
            if (!LlmVendors.IsKnown(request.Vendor))
            {
                return Results.BadRequest(new { error = "Unknown or missing vendor." });
            }
            if (string.IsNullOrWhiteSpace(request.Model))
            {
                return Results.BadRequest(new { error = "A model (or Azure deployment name) is required." });
            }
            if (string.IsNullOrWhiteSpace(request.CredentialRef))
            {
                return Results.BadRequest(new { error = "An API-key credential is required." });
            }

            var config = new AiProviderConfig(
                request.Vendor!,
                request.Model!.Trim(),
                request.CredentialRef!.Trim(),
                string.IsNullOrWhiteSpace(request.BaseUrl) ? null : request.BaseUrl!.Trim(),
                string.IsNullOrWhiteSpace(request.ApiVersion) ? null : request.ApiVersion!.Trim(),
                request.MaxTokens);

            await store.SetAsync(config, ct);
            return Results.Ok(new AiProviderConfigResponse(
                config.Vendor, config.Model, config.CredentialRef, config.BaseUrl, config.ApiVersion, config.MaxTokens, LlmVendors.All));
        });

        // Test the supplied provider config end-to-end: resolve the key, hit the vendor with a tiny
        // completion, and report success/failure + round-trip latency. Tests exactly what's in the form
        // (so it works before Save). Never 500s on a provider/key error — that outcome is the answer.
        app.MapPost("/api/settings/ai-provider/test", async (
            SetAiProviderConfigRequest request,
            System.Collections.Generic.IEnumerable<ILlmChatProvider> providers,
            Knotarium.Core.Contracts.ISecretResolver secretResolver,
            CancellationToken ct) =>
        {
            if (!LlmVendors.IsKnown(request.Vendor))
            {
                return Results.Ok(new AiProviderTestResponse(false, "Unknown or missing vendor.", null, null));
            }
            if (string.IsNullOrWhiteSpace(request.Model))
            {
                return Results.Ok(new AiProviderTestResponse(false, "A model (or Azure deployment name) is required.", null, request.Model));
            }
            if (string.IsNullOrWhiteSpace(request.CredentialRef))
            {
                return Results.Ok(new AiProviderTestResponse(false, "An API-key credential is required.", null, request.Model));
            }

            var provider = System.Linq.Enumerable.FirstOrDefault(providers, p => p.Vendor == request.Vendor);
            if (provider is null)
            {
                return Results.Ok(new AiProviderTestResponse(false, $"No adapter is registered for vendor '{request.Vendor}'.", null, request.Model));
            }

            var apiKey = await secretResolver.ResolveAsync(request.CredentialRef!, ct);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return Results.Ok(new AiProviderTestResponse(false, "The API key could not be resolved from the selected credential.", null, request.Model));
            }

            var config = new AiProviderConfig(
                request.Vendor!, request.Model!.Trim(), request.CredentialRef!.Trim(),
                string.IsNullOrWhiteSpace(request.BaseUrl) ? null : request.BaseUrl!.Trim(),
                string.IsNullOrWhiteSpace(request.ApiVersion) ? null : request.ApiVersion!.Trim(),
                request.MaxTokens);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var reply = await provider.CompleteAsync(
                    new LlmChatRequest("You are a connection test for an automation platform.",
                        "Reply with the single word: OK", config, apiKey!, 16), ct);
                stopwatch.Stop();
                var trimmed = (reply ?? string.Empty).Trim();
                return Results.Ok(new AiProviderTestResponse(
                    true, $"Connected. Model replied: \"{Truncate(trimmed, 60)}\".", (int)stopwatch.ElapsedMilliseconds, config.Model));
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return Results.Ok(new AiProviderTestResponse(false, Truncate(ex.Message, 300), (int)stopwatch.ElapsedMilliseconds, config.Model));
            }
        });

        // Best-effort live model listing for the supplied config. Additive to the frontend's curated list;
        // an empty result just means "no live models — use the curated suggestions".
        app.MapPost("/api/settings/ai-provider/models", async (
            SetAiProviderConfigRequest request,
            System.Net.Http.IHttpClientFactory clientFactory,
            Knotarium.Core.Contracts.ISecretResolver secretResolver,
            CancellationToken ct) =>
        {
            if (!LlmVendors.IsKnown(request.Vendor) || string.IsNullOrWhiteSpace(request.CredentialRef))
            {
                return Results.Ok(new AiProviderModelsResponse(Array.Empty<string>()));
            }
            var apiKey = await secretResolver.ResolveAsync(request.CredentialRef!, ct);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return Results.Ok(new AiProviderModelsResponse(Array.Empty<string>()));
            }
            var models = await AiModelCatalog.ListAsync(
                clientFactory, request.Vendor!, apiKey!, request.BaseUrl, request.ApiVersion, ct);
            return Results.Ok(new AiProviderModelsResponse(models));
        });
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
