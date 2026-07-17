// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Features.Ai.Providers;

/// <summary>
/// Best-effort live model listing per vendor, for the "load live models" affordance in the AI provider
/// settings. Purely additive to the frontend's curated list — a failure (bad key, offline, a vendor with no
/// listable models API such as Azure deployments) returns an empty list rather than throwing, so the UI just
/// falls back to the curated suggestions. Reuses the egress-policed "HttpNode" client like the chat providers.
/// </summary>
public static class AiModelCatalog
{
    public static async Task<IReadOnlyList<string>> ListAsync(
        IHttpClientFactory clientFactory,
        string vendor,
        string apiKey,
        string? baseUrl,
        string? apiVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            return vendor switch
            {
                LlmVendors.Anthropic => await AnthropicAsync(clientFactory, apiKey, baseUrl, apiVersion, cancellationToken),
                LlmVendors.OpenAi => await OpenAiAsync(clientFactory, apiKey, baseUrl, cancellationToken),
                LlmVendors.Gemini => await GeminiAsync(clientFactory, apiKey, baseUrl, cancellationToken),
                // Azure deployments are named by the operator and not enumerable via the data-plane key.
                _ => Array.Empty<string>(),
            };
        }
        catch (Exception)
        {
            // Best effort only — the curated list is always there as a fallback.
            return Array.Empty<string>();
        }
    }

    private static async Task<IReadOnlyList<string>> AnthropicAsync(
        IHttpClientFactory clientFactory, string apiKey, string? baseUrl, string? apiVersion, CancellationToken ct)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.anthropic.com" : baseUrl!.TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{root}/v1/models?limit=100");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", string.IsNullOrWhiteSpace(apiVersion) ? "2023-06-01" : apiVersion);
        return await SendAndReadIdsAsync(clientFactory, req, "data", "id", ct);
    }

    private static async Task<IReadOnlyList<string>> OpenAiAsync(
        IHttpClientFactory clientFactory, string apiKey, string? baseUrl, CancellationToken ct)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com" : baseUrl!.TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{root}/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return await SendAndReadIdsAsync(clientFactory, req, "data", "id", ct);
    }

    private static async Task<IReadOnlyList<string>> GeminiAsync(
        IHttpClientFactory clientFactory, string apiKey, string? baseUrl, CancellationToken ct)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? "https://generativelanguage.googleapis.com" : baseUrl!.TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{root}/v1beta/models?pageSize=100");
        req.Headers.Add("x-goog-api-key", apiKey);
        // Gemini uses "models" with a "name" like "models/gemini-2.0-flash"; strip the prefix.
        var names = await SendAndReadIdsAsync(clientFactory, req, "models", "name", ct);
        return names.Select(n => n.StartsWith("models/", StringComparison.Ordinal) ? n["models/".Length..] : n).ToList();
    }

    private static async Task<IReadOnlyList<string>> SendAndReadIdsAsync(
        IHttpClientFactory clientFactory, HttpRequestMessage request, string arrayField, string idField, CancellationToken ct)
    {
        var client = clientFactory.CreateClient("HttpNode");
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<string>();
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty(arrayField, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var ids = new List<string>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.TryGetProperty(idField, out var id) && id.ValueKind == JsonValueKind.String)
            {
                var value = id.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    ids.Add(value!);
                }
            }
        }
        return ids;
    }
}
