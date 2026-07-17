// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.Options;
using Knotarium.Core.Contracts.OpenApi;
using Microsoft.Extensions.Logging;

namespace Knotarium.Features.Options;

/// <summary>
/// Generic design-time options loader: resolves a stored <c>ServerConfig</c> (BaseUrl + credential),
/// performs a single REST <c>GET</c> against a relative resource path, and maps each entry of the
/// returned JSON array to an <see cref="OptionItem"/>. New integrations reuse this by configuring the
/// path / field mappings via <see cref="OptionLoadContext.DependsOn"/> rather than writing a new loader.
/// </summary>
public sealed class RestCollectionOptionsLoader : IOptionsLoader
{
    public const string LoaderName = "rest.collection";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerConfigStore _serverConfigStore;
    private readonly ICredentialAccessor _credentialAccessor;
    private readonly ILogger<RestCollectionOptionsLoader> _logger;

    public RestCollectionOptionsLoader(
        IHttpClientFactory httpClientFactory,
        IServerConfigStore serverConfigStore,
        ICredentialAccessor credentialAccessor,
        ILogger<RestCollectionOptionsLoader> logger)
    {
        _httpClientFactory = httpClientFactory;
        _serverConfigStore = serverConfigStore;
        _credentialAccessor = credentialAccessor;
        _logger = logger;
    }

    public string Name => LoaderName;

    public async Task<OptionListResult> LoadAsync(OptionLoadContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.ConnectionId))
        {
            throw new OptionsLoadException("No connection selected. Choose a server configuration first.");
        }

        var serverConfig = await _serverConfigStore.GetAsync(context.ConnectionId, cancellationToken)
            ?? throw new OptionsLoadException($"Server configuration '{context.ConnectionId}' was not found.");

        // dependsOn is untrusted: read configuration defensively and validate the path.
        var path = GetDependsOn(context, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new OptionsLoadException("This option loader requires a 'path' to the resource collection.");
        }

        var resolvedPath = SubstitutePlaceholders(path, context);
        var relativePath = SanitizeRelativePath(resolvedPath);
        var labelField = GetDependsOn(context, "labelField") ?? "name";
        var valueField = GetDependsOn(context, "valueField") ?? "id";
        var collectionField = GetDependsOn(context, "collectionField"); // dotted path to the array, optional

        var requestUri = BuildRequestUri(serverConfig.BaseUrl, relativePath, context.Search);

        // Honor the server config's opt-out for TLS validation (self-signed / untrusted cert),
        // the same as the OpenAPI node's runtime calls. Egress policy still applies either way.
        var client = _httpClientFactory.CreateClient(serverConfig.AllowInsecureCertificate ? "InsecureHttp" : "HttpNode");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        await ApplyCredentialAsync(request, serverConfig.CredentialRef, cancellationToken);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new OptionsLoadException($"Could not reach the resource system: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new OptionsLoadException(
                    $"Resource system returned status {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var options = ParseOptions(json, collectionField, labelField, valueField);
            return new OptionListResult(options);
        }
    }

    private static string? GetDependsOn(OptionLoadContext context, string key)
        => context.DependsOn != null && context.DependsOn.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Replaces <c>{name}</c> segments in a cascading collection path with the corresponding
    /// dependsOn (parent) values, URL-escaped. An unresolved placeholder means the parent hasn't
    /// been selected yet — fail with a clear, actionable message (manual entry stays available).
    /// </summary>
    private static string SubstitutePlaceholders(string path, OptionLoadContext context)
    {
        if (!path.Contains('{')) return path;

        return PlaceholderPattern.Replace(path, match =>
        {
            var name = match.Groups[1].Value;
            var value = GetDependsOn(context, name);
            if (string.IsNullOrEmpty(value))
            {
                throw new OptionsLoadException($"Select a value for '{name}' first to load these options.");
            }
            return Uri.EscapeDataString(value);
        });
    }

    private static readonly System.Text.RegularExpressions.Regex PlaceholderPattern =
        new(@"\{([^{}/]+)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Rejects absolute URLs and path-traversal so an untrusted <c>path</c> cannot redirect the
    /// request off the stored BaseUrl host.
    /// </summary>
    private static string SanitizeRelativePath(string path)
    {
        var trimmed = path.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            throw new OptionsLoadException("Resource path must be relative to the server's base URL.");
        }

        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            throw new OptionsLoadException("Resource path must not contain '..' segments.");
        }

        return trimmed.StartsWith('/') ? trimmed[1..] : trimmed;
    }

    private static Uri BuildRequestUri(string baseUrl, string relativePath, string? search)
    {
        var normalizedBase = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        var builder = new UriBuilder(new Uri(new Uri(normalizedBase), relativePath));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var encoded = Uri.EscapeDataString(search);
            builder.Query = string.IsNullOrEmpty(builder.Query)
                ? $"search={encoded}"
                : $"{builder.Query.TrimStart('?')}&search={encoded}";
        }

        return builder.Uri;
    }

    private async Task ApplyCredentialAsync(HttpRequestMessage request, string? credentialRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credentialRef)) return;

        var secret = await _credentialAccessor.GetSecretAsync(credentialRef, ct);
        if (!string.IsNullOrEmpty(secret))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
    }

    private List<OptionItem> ParseOptions(string json, string? collectionField, string labelField, string valueField)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<OptionItem>();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new OptionsLoadException($"Resource system returned malformed JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var collection = ResolveCollection(document.RootElement, collectionField);
            if (collection.ValueKind != JsonValueKind.Array)
            {
                throw new OptionsLoadException("Expected a JSON array of resources from the resource system.");
            }

            var items = new List<OptionItem>();
            foreach (var element in collection.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                var value = ReadField(element, valueField);
                var label = ReadField(element, labelField) ?? value;
                if (value == null) continue; // a resource with no stable key cannot be selected

                items.Add(new OptionItem(label ?? value, value));
            }

            return items;
        }
    }

    private static JsonElement ResolveCollection(JsonElement root, string? collectionField)
    {
        if (string.IsNullOrWhiteSpace(collectionField)) return root;

        var current = root;
        foreach (var segment in collectionField.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                throw new OptionsLoadException($"Could not find collection field '{collectionField}' in the response.");
            }
            current = next;
        }
        return current;
    }

    private static string? ReadField(JsonElement element, string field)
    {
        if (!element.TryGetProperty(field, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => prop.GetRawText(),
        };
    }
}

/// <summary>
/// Signals a loader failure that the design-time endpoint should translate into the
/// <c>SYSTEM_UNREACHABLE</c> error envelope (still HTTP 200) rather than a 5xx.
/// </summary>
public sealed class OptionsLoadException : Exception
{
    public OptionsLoadException(string message) : base(message) { }
    public OptionsLoadException(string message, Exception inner) : base(message, inner) { }
}
