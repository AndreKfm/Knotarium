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

/// <summary>Generate/modify the body of an Inline Code node from a natural-language prompt.
/// <see cref="CurrentCode"/> (optional) is the code already in the editor, given to the model as
/// context so it can extend/refactor rather than start from scratch.</summary>
public sealed record GenerateInlineCodeRequest(string Prompt, string? CurrentCode, string? Language);

public sealed record GenerateInlineCodeResponse(string Code);

public static class AiGenerationEndpoint
{
    // The Inline Code node wraps the script body in a method with these symbols in scope; the model
    // must target exactly this contract, and only APIs the banned-API screen permits.
    private const string InlineCodeSystemPrompt = """
        You write the BODY of a C# "Inline Code" node for the Knotarium automation platform. Your code is
        inserted verbatim into an async method, so write statements — not a class or a Main method.

        In scope (already provided — do NOT redeclare):
        - Input.Get<T>("name")  -> read a node input (e.g. var n = Input.Get<int>("count");)
        - Logger                -> Microsoft.Extensions.Logging.ILogger (Logger.LogInformation(...))
        - cancellationToken     -> pass to any awaited call
        - Success(object? payload)  -> return this as the node's output, e.g. return Success(new { total });
        - Fail(string error)        -> return this to fail the node, e.g. return Fail("no data");

        Already imported (don't repeat): System, System.Collections.Generic, System.Linq, System.Text.Json,
        System.Threading, System.Threading.Tasks, Microsoft.Extensions.Logging, Knotarium.Core.Contracts,
        Knotarium.Core.Domain. You MAY add other `using` lines at the very top; they are hoisted.

        End every path by returning Success(...) or Fail(...). The last statement is typically a return.

        FORBIDDEN (the code is rejected before it runs): System.IO, System.Diagnostics, System.Reflection.Emit,
        System.Net.Sockets, System.Runtime.InteropServices, System.Runtime.Loader, Microsoft.Win32, and any
        static mutable state. HTTP is allowed via System.Net.Http.

        Output ONLY the C# code. No markdown fences, no prose, no comments explaining what you did.
        """;

    public static void MapAiGenerationEndpoints(this WebApplication app)
    {
        // One-shot code generation for the Inline Code editor's "Generate with AI". Synchronous (a single
        // completion, unlike the async workflow-generation job) so the editor can drop the result straight in.
        app.MapPost("/api/ai/inline-code", async (
            GenerateInlineCodeRequest request,
            Knotarium.Core.Contracts.Ai.IChatCompletionService chat,
            CancellationToken ct) =>
        {
            var prompt = request.Prompt?.Trim() ?? string.Empty;
            if (prompt.Length == 0)
            {
                return Results.BadRequest(new { error = "Describe what the code should do." });
            }
            if (prompt.Length > 4000)
            {
                return Results.BadRequest(new { error = "Prompt exceeds the 4000-character maximum." });
            }

            var user = string.IsNullOrWhiteSpace(request.CurrentCode)
                ? prompt
                : $"Current code:\n```\n{request.CurrentCode!.Trim()}\n```\n\nModify or extend it to: {prompt}";

            try
            {
                var reply = await chat.CompleteAsync(
                    new Knotarium.Core.Contracts.Ai.ChatCompletionRequest(InlineCodeSystemPrompt, user), ct);
                return Results.Ok(new GenerateInlineCodeResponse(StripCodeFences(reply)));
            }
            catch (System.InvalidOperationException ex)
            {
                // Not-configured / bad key / provider transport — not repairable by retrying. Surface plainly.
                return Results.BadRequest(new { error = ex.Message });
            }
        });

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

    /// <summary>Models often wrap code in a ```csharp … ``` block despite instructions; peel it off so the
    /// editor gets pure code. Leaves un-fenced replies untouched.</summary>
    private static string StripCodeFences(string? reply)
    {
        var text = (reply ?? string.Empty).Trim();
        if (!text.StartsWith("```", System.StringComparison.Ordinal))
        {
            return text;
        }
        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
        {
            return text;
        }
        var body = text[(firstNewline + 1)..];
        var lastFence = body.LastIndexOf("```", System.StringComparison.Ordinal);
        return (lastFence >= 0 ? body[..lastFence] : body).Trim();
    }
}
