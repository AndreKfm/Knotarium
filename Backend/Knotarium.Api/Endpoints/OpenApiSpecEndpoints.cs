// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Knotarium.Core.Contracts.OpenApi;
using Knotarium.Features.OpenApi;

namespace Knotarium.Api;

/// <summary>
/// OpenAPI importer endpoints: ingest a spec (multipart file, inline content, or server-side URL
/// fetch through the SSRF-guarded egress client), then read back grouped operations, versions, and
/// per-operation resource-locator suggestions used by the HTTP node authoring UI.
/// </summary>
public static class OpenApiSpecEndpoints
{
    public static void MapOpenApiSpecEndpoints(this WebApplication app)
    {
        // POST /api/openapi/specs  — multipart (file) or JSON { "content": "..." }
        app.MapPost("/api/openapi/specs", async (HttpRequest request, ImportOpenApiSpecHandler handler, IOpenApiSpecStore store, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            ReadOnlyMemory<byte> rawContent;
            string? specIdOverride = null;

            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(ct);
                var file = form.Files.GetFile("file");
                if (file is null)
                    return Results.BadRequest(new { message = "No file provided." });
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                rawContent = ms.ToArray();
                specIdOverride = form["specId"];
            }
            else
            {
                var body = await request.ReadFromJsonAsync<ImportSpecRequest>(ct);
                if (body is null)
                    return Results.BadRequest(new { message = "Request body must contain 'content' or 'url'." });

                specIdOverride = body.SpecId;

                if (!string.IsNullOrWhiteSpace(body.Url))
                {
                    // Fetch the spec server-side (browsers hit CORS on most spec URLs). The "HttpNode"
                    // client runs through the egress policy, so SSRF protection (private/loopback blocking)
                    // applies exactly as it does to node HTTP calls.
                    if (!Uri.TryCreate(body.Url.Trim(), UriKind.Absolute, out var specUri)
                        || (specUri.Scheme != Uri.UriSchemeHttp && specUri.Scheme != Uri.UriSchemeHttps))
                    {
                        return Results.BadRequest(new { message = "Provide an absolute http(s) URL." });
                    }

                    try
                    {
                        // Per-request opt-in for self-signed/untrusted certs; egress policy still applies.
                        var client = httpClientFactory.CreateClient(body.AllowInsecureCertificate ? "InsecureHttp" : "HttpNode");
                        using var response = await client.GetAsync(specUri, ct);
                        if (!response.IsSuccessStatusCode)
                            return Results.BadRequest(new { message = $"Could not fetch spec: {(int)response.StatusCode} {response.ReasonPhrase}." });
                        rawContent = await response.Content.ReadAsByteArrayAsync(ct);
                    }
                    catch (HttpRequestException ex)
                    {
                        return Results.BadRequest(new { message = $"Could not fetch spec from URL: {ex.Message}" });
                    }

                    if (rawContent.Length == 0)
                        return Results.BadRequest(new { message = "Fetched spec was empty." });
                }
                else if (!string.IsNullOrWhiteSpace(body.Content))
                {
                    rawContent = System.Text.Encoding.UTF8.GetBytes(body.Content);
                }
                else
                {
                    return Results.BadRequest(new { message = "Request body must contain 'content' or 'url'." });
                }
            }

            try
            {
                var saved = await handler.HandleAsync(rawContent, specIdOverride, ct);
                var full = await store.GetLatestAsync(saved.Id, ct);
                if (full is null) return Results.Problem("Spec was saved but could not be retrieved.");
                var groups = OpenApiGrouper.Group(full.Value.Full.Operations);
                return Results.Ok(new ImportSpecResponse(
                    saved.Id.Value, saved.SpecVersionNumber, saved.Title,
                    saved.OriginalFormat, groups, full.Value.Full.Schemas,
                    saved.DefaultServers));
            }
            catch (Knotarium.Core.Exceptions.OpenApiParseException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        // GET /api/openapi/specs
        app.MapGet("/api/openapi/specs", async (IOpenApiSpecStore store, CancellationToken ct) =>
        {
            var list = await store.ListAsync(ct);
            var result = list.Select(s => new SpecSummaryResponse(
                s.Id.Value, s.Title, s.Version, s.SpecVersionNumber, s.ImportedAtUtc, s.OriginalFormat));
            return Results.Ok(result);
        });

        // GET /api/openapi/specs/{id}
        app.MapGet("/api/openapi/specs/{id}", async (string id, IOpenApiSpecStore store, CancellationToken ct) =>
        {
            var result = await store.GetLatestAsync(new Knotarium.Core.Domain.OpenApi.OpenApiSpecId(id), ct);
            if (result is null) return Results.NotFound(new { message = $"Spec '{id}' not found." });
            var groups = OpenApiGrouper.Group(result.Value.Full.Operations);
            return Results.Ok(new ImportSpecResponse(
                result.Value.Spec.Id.Value, result.Value.Spec.SpecVersionNumber,
                result.Value.Spec.Title, result.Value.Spec.OriginalFormat,
                groups, result.Value.Full.Schemas,
                result.Value.Spec.DefaultServers));
        });

        // GET /api/openapi/specs/{id}/versions
        app.MapGet("/api/openapi/specs/{id}/versions", async (string id, IOpenApiSpecStore store, CancellationToken ct) =>
        {
            var specId = new Knotarium.Core.Domain.OpenApi.OpenApiSpecId(id);
            var versions = await store.GetVersionsAsync(specId, ct);
            var result = versions.Select(s => new SpecSummaryResponse(
                s.Id.Value, s.Title, s.Version, s.SpecVersionNumber, s.ImportedAtUtc, s.OriginalFormat));
            return Results.Ok(result);
        });

        // GET /api/openapi/specs/{id}/operations/{operationId}
        app.MapGet("/api/openapi/specs/{id}/operations/{operationId}", async (string id, string operationId, IOpenApiSpecStore store, CancellationToken ct) =>
        {
            var specId = new Knotarium.Core.Domain.OpenApi.OpenApiSpecId(id);
            var op = await store.GetOperationAsync(specId, operationId, ct);
            if (op is null) return Results.NotFound(new { message = $"Operation '{operationId}' not found in spec '{id}'." });
            return Results.Ok(op);
        });

        // GET /api/openapi/specs/{id}/operations/{operationId}/locator-suggestions
        // Spec-derived hints: which path params can be picked from a sibling collection endpoint.
        app.MapGet("/api/openapi/specs/{id}/operations/{operationId}/locator-suggestions", async (string id, string operationId, IOpenApiSpecStore store, CancellationToken ct) =>
        {
            var specId = new Knotarium.Core.Domain.OpenApi.OpenApiSpecId(id);
            var latest = await store.GetLatestAsync(specId, ct);
            if (latest is null) return Results.NotFound(new { message = $"Spec '{id}' not found." });

            var operation = latest.Value.Full.Operations.FirstOrDefault(o => o.OperationId == operationId);
            if (operation is null) return Results.NotFound(new { message = $"Operation '{operationId}' not found in spec '{id}'." });

            var suggestions = Knotarium.Features.OpenApi.ResourceLocatorInference.Suggest(latest.Value.Full, operation);
            return Results.Ok(suggestions);
        });

        // DELETE /api/openapi/specs/{id}
        app.MapDelete("/api/openapi/specs/{id}", async (string id, DeleteOpenApiSpecHandler handler, CancellationToken ct) =>
        {
            var deleted = await handler.HandleAsync(id, ct);
            return deleted ? Results.NoContent() : Results.NotFound(new { message = $"Spec '{id}' not found." });
        });
    }
}
