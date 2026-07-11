using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Polling;

/// <summary>Polls an arbitrary HTTP endpoint. Mirrors HttpRequestNodeTask for client/credential handling.</summary>
public sealed class HttpPollSource : IPollSource
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly ISecretResolver _secretResolver;

    public HttpPollSource(IHttpClientFactory clientFactory, ISecretResolver secretResolver)
    {
        _clientFactory = clientFactory;
        _secretResolver = secretResolver;
    }

    public string Kind => "http";

    public async Task<PollResult> PollAsync(PollContext context, CancellationToken cancellationToken)
    {
        using var configDoc = JsonDocument.Parse(context.ConfigJson);
        var root = configDoc.RootElement;

        var url = GetString(root, "url") ?? throw new InvalidOperationException("Polling HTTP source is missing 'url'.");
        var method = GetString(root, "method") ?? "GET";
        var strategy = PollStrategyParser.Parse(GetString(root, "changeDetection"));
        var jsonPath = GetString(root, "jsonCursorPath");

        var client = _clientFactory.CreateClient("HttpNode");
        var request = new HttpRequestMessage(new HttpMethod(method), url);

        ApplyHeaders(request, GetString(root, "headersJson"));
        await ApplyCredentialAsync(request, GetString(root, "apiKeySecretRef"), cancellationToken);
        ApplyConditionalHeaders(request, strategy, context.Cursor);

        var response = await client.SendAsync(request, cancellationToken);

        if ((strategy == PollChangeDetection.Etag || strategy == PollChangeDetection.LastModified)
            && response.StatusCode == HttpStatusCode.NotModified)
        {
            return new PollResult(HasNew: false, Payload: null, NewCursor: context.Cursor);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        return strategy switch
        {
            PollChangeDetection.Etag => PollValidator.FromValidator(response.Headers.ETag?.Tag, context.Cursor, body),
            // "R" (RFC-1123) so the stored cursor is a valid HTTP-date when sent back as If-Modified-Since.
            PollChangeDetection.LastModified => PollValidator.FromValidator(
                response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture), context.Cursor, body),
            _ => BodyChangeDetector.Detect(strategy, body, context.Cursor, jsonPath)
        };
    }

    private static void ApplyConditionalHeaders(HttpRequestMessage request, PollChangeDetection strategy, string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return;
        }

        if (strategy == PollChangeDetection.Etag)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cursor);
        }
        else if (strategy == PollChangeDetection.LastModified)
        {
            request.Headers.TryAddWithoutValidation("If-Modified-Since", cursor);
        }
    }

    private static void ApplyHeaders(HttpRequestMessage request, string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return;
        }

        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
        if (headers is null)
        {
            return;
        }

        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private async Task ApplyCredentialAsync(HttpRequestMessage request, string? secretRef, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(secretRef))
        {
            return;
        }

        var secret = await _secretResolver.ResolveAsync(secretRef, ct);
        if (!string.IsNullOrEmpty(secret))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
}
