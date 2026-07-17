// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.OpenApi;
using Knotarium.Core.Domain.OpenApi;
using Knotarium.Core.Exceptions;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Microsoft.OpenApi.YamlReader;

namespace Knotarium.Infrastructure.OpenApi;

public sealed class MicrosoftOpenApiParser : IOpenApiParser
{
    private static readonly OpenApiReaderSettings ReaderSettings = CreateSettings();

    private static OpenApiReaderSettings CreateSettings()
    {
        var settings = new OpenApiReaderSettings();
        settings.TryAddReader("yaml", new OpenApiYamlReader());
        return settings;
    }

    public Task<ParsedSpec> ParseAsync(ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var text = Encoding.UTF8.GetString(content.Span);
        var format = text.TrimStart().StartsWith("{") ? "json" : "yaml";

        ReadResult readResult;
        try
        {
            readResult = OpenApiDocument.Parse(text, format, ReaderSettings);
        }
        catch (Exception ex)
        {
            throw new OpenApiParseException("Failed to parse OpenAPI document.", ex);
        }

        var diagnostic = readResult.Diagnostic!;
        // Tolerate non-unique path signatures (e.g. `/x/{aId}` and `/x/{bId}` collide because
        // OpenAPI ignores parameter names when comparing paths). Many real-world vendor specs
        // have this; the paths are still distinct dictionary keys with distinct operationIds, and
        // we route by operationId, not by matching an incoming URL — so it is safe to import them.
        var fatalErrors = diagnostic.Errors
            .Where(e => !IsTolerablePathUniquenessError(e))
            .ToList();
        if (fatalErrors.Count > 0)
        {
            var messages = string.Join("; ", fatalErrors.Select(e => e.Message));
            throw new OpenApiParseException($"OpenAPI parse errors: {messages}");
        }

        var document = readResult.Document;
        if (document is null)
            throw new OpenApiParseException("Parser returned a null document.");

        DetectExternalRefs(document);

        var originalFormat = diagnostic.SpecificationVersion switch
        {
            OpenApiSpecVersion.OpenApi2_0 => "swagger2.0",
            OpenApiSpecVersion.OpenApi3_0 => "openapi3.0",
            _ => "openapi3.1"
        };

        return Task.FromResult(Normalize(document, originalFormat));
    }

    // A validation error of the form "The path signature '...' MUST be unique." — non-unique
    // paths that differ only by parameter name. Tolerated; see ParseAsync for rationale.
    private static bool IsTolerablePathUniquenessError(OpenApiError error)
    {
        var msg = error.Message ?? string.Empty;
        return msg.Contains("path signature", StringComparison.OrdinalIgnoreCase)
            && msg.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // External $ref detection
    // In v2, external refs cannot be resolved (no LoadExternalRefs) and remain
    // unresolved — UnresolvedReference==true is the reliable detection signal.
    // -------------------------------------------------------------------------

    private static void DetectExternalRefs(OpenApiDocument document)
    {
        var visited = new HashSet<IOpenApiSchema>(ReferenceEqualityComparer.Instance);

        foreach (var schema in (document.Components?.Schemas ?? new Dictionary<string, IOpenApiSchema>()).Values)
            CheckSchema(schema, visited);

        foreach (var pathItem in (document.Paths ?? new OpenApiPaths()).Values)
        {
            foreach (var (_, operation) in pathItem.Operations ?? new Dictionary<System.Net.Http.HttpMethod, OpenApiOperation>())
            {
                foreach (var param in operation.Parameters ?? [])
                {
                    CheckUnresolved(param);
                    if (param is OpenApiParameter cp)
                        CheckSchema(cp.Schema, visited);
                }

                if (operation.RequestBody is not null)
                {
                    CheckUnresolved(operation.RequestBody);
                    if (operation.RequestBody is OpenApiRequestBody rb && rb.Content != null)
                        foreach (var mt in rb.Content.Values)
                            CheckSchema(mt.Schema, visited);
                }

                if (operation.Responses != null)
                    foreach (var response in operation.Responses.Values)
                    {
                        CheckUnresolved(response);
                        if (response is OpenApiResponse cr && cr.Content != null)
                            foreach (var mt in cr.Content.Values)
                                CheckSchema(mt.Schema, visited);
                    }
            }
        }
    }

    private static void CheckUnresolved(object? obj)
    {
        if (obj is IOpenApiReferenceHolder holder && holder.UnresolvedReference)
            throw new OpenApiParseException($"External $ref not supported: unresolved reference in imported spec.");
    }

    private static void CheckSchema(IOpenApiSchema? schema, HashSet<IOpenApiSchema> visited)
    {
        if (schema is null || !visited.Add(schema)) return;

        if (schema is IOpenApiReferenceHolder holder && holder.UnresolvedReference)
            throw new OpenApiParseException($"External $ref not supported: unresolved schema reference in imported spec.");

        if (schema is OpenApiSchemaReference)
            return; // resolved local ref — stop walking to avoid cycles

        if (schema is OpenApiSchema inlineSchema)
        {
            foreach (var prop in (inlineSchema.Properties ?? new Dictionary<string, IOpenApiSchema>()).Values)
                CheckSchema(prop, visited);
            CheckSchema(inlineSchema.Items, visited);
        }
    }

    // -------------------------------------------------------------------------
    // Normalization
    // -------------------------------------------------------------------------

    private static ParsedSpec Normalize(OpenApiDocument doc, string originalFormat)
    {
        var servers = (doc.Servers ?? [])
            .Select(s => s.Url)
            .Where(u => !string.IsNullOrEmpty(u))
            .Select(u => u!)
            .ToList();

        var operations = new List<ApiOperation>();
        foreach (var (pathTemplate, pathItem) in doc.Paths ?? new OpenApiPaths())
        {
            foreach (var (method, operation) in pathItem.Operations ?? new Dictionary<System.Net.Http.HttpMethod, OpenApiOperation>())
            {
                var methodStr = method.Method.ToUpperInvariant();
                var operationId = string.IsNullOrEmpty(operation.OperationId)
                    ? $"{method.Method.ToLowerInvariant()}_{pathTemplate.Replace("/", "_").TrimStart('_')}"
                    : operation.OperationId;

                var parameters = (operation.Parameters ?? [])
                    .OfType<OpenApiParameter>()
                    .Select(p => new ApiParameter(
                        p.Name ?? "",
                        MapParameterLocation(p.In),
                        p.Required,
                        p.Description,
                        SerializeSchema(p.Schema)))
                    .ToList();

                ApiRequestBody? requestBody = null;
                if (operation.RequestBody is OpenApiRequestBody rb)
                {
                    var mediaTypes = rb.Content?.Keys.ToList() ?? [];
                    var bodySchema = rb.Content?.Values.FirstOrDefault()?.Schema;
                    requestBody = new ApiRequestBody(rb.Required, mediaTypes, SerializeSchema(bodySchema));
                }

                var securityRefs = (operation.Security ?? [])
                    .SelectMany(s => s.Keys.Select(k => k.Reference?.Id ?? ""))
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct()
                    .ToList();

                var tags = operation.Tags is null
                    ? new List<string>()
                    : operation.Tags
                        .Select(t => t.Name)
                        .Where(t => !string.IsNullOrEmpty(t))
                        .Select(t => t!)
                        .ToList();

                operations.Add(new ApiOperation(
                    operationId,
                    methodStr,
                    pathTemplate,
                    operation.Summary,
                    tags,
                    parameters,
                    requestBody,
                    securityRefs));
            }
        }

        var allTags = operations
            .SelectMany(o => o.Tags)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        var schemas = (doc.Components?.Schemas ?? new Dictionary<string, IOpenApiSchema>())
            .Select(kvp => new ApiSchema(
                kvp.Key,
                (kvp.Value as OpenApiSchema)?.Description,
                SerializeSchema(kvp.Value)))
            .ToList();

        var securitySchemes = (doc.Components?.SecuritySchemes ?? new Dictionary<string, IOpenApiSecurityScheme>())
            .Where(kvp => kvp.Value is OpenApiSecurityScheme)
            .Select(kvp => MapSecurityScheme(kvp.Key, (OpenApiSecurityScheme)kvp.Value))
            .ToList();

        var title = doc.Info?.Title ?? "";
        var metadata = new ImportedSpec(
            new OpenApiSpecId(SlugifySpecId(title)),
            title,
            doc.Info?.Version ?? "",
            originalFormat,
            servers,
            allTags,
            DateTimeOffset.UtcNow,
            1);

        return new ParsedSpec(metadata, operations, schemas, securitySchemes);
    }

    private static string SlugifySpecId(string title)
    {
        var slug = System.Text.RegularExpressions.Regex
            .Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');
        return string.IsNullOrEmpty(slug) ? "spec" : slug;
    }

    private static string MapParameterLocation(ParameterLocation? location) => location switch
    {
        ParameterLocation.Path => "path",
        ParameterLocation.Query => "query",
        ParameterLocation.Header => "header",
        ParameterLocation.Cookie => "cookie",
        _ => "query"
    };

    private static SecurityScheme MapSecurityScheme(string name, OpenApiSecurityScheme scheme)
    {
        var type = scheme.Type switch
        {
            SecuritySchemeType.ApiKey => "apiKey",
            SecuritySchemeType.Http => "http",
            SecuritySchemeType.OAuth2 => "oauth2",
            SecuritySchemeType.OpenIdConnect => "openIdConnect",
            _ => "unknown"
        };

        var inValue = scheme.In switch
        {
            ParameterLocation.Header => "header",
            ParameterLocation.Query => "query",
            ParameterLocation.Cookie => "cookie",
            _ => null
        };

        var tokenUrl = scheme.Flows?.ClientCredentials?.TokenUrl?.ToString()
            ?? scheme.Flows?.Password?.TokenUrl?.ToString();

        return new SecurityScheme(name, type, scheme.Scheme, inValue, scheme.Name, tokenUrl);
    }

    private static string SerializeSchema(IOpenApiSchema? schema)
    {
        if (schema is not IOpenApiSerializable serializable) return "{}";
        try
        {
            using var ms = new MemoryStream();
            using var sw = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);
            var jsonWriter = new OpenApiJsonWriter(sw);
            serializable.SerializeAsV3(jsonWriter);
            sw.Flush();
            ms.Position = 0;
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return "{}";
        }
    }
}
