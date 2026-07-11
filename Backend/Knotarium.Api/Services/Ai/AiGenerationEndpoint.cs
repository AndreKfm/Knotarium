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
    }
}
