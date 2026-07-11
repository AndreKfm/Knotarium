using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Contracts.OpenApi;
using KnotGarden.Core.Domain;

namespace KnotGarden.Features.OpenApi;

/// <summary>
/// Deterministically generates a <see cref="GeneratedPackage"/> from a <see cref="ParsedSpec"/>.
/// No I/O — pure transformation.
/// </summary>
public sealed class OpenApiNodeGenerator
{
    public GeneratedPackage Generate(ParsedSpec spec)
    {
        var packageId = BuildPackageId(spec.Metadata.Id.Value);
        var manifestYaml = BuildManifestYaml(packageId, spec);
        // Option C: no executor source is emitted. All openapi.* nodes run through the
        // single pre-compiled OpenApiInterpreterExecutor (tier: Interpreted), so there is
        // no per-spec C# to compile.
        return new GeneratedPackage(packageId, manifestYaml, string.Empty);
    }

    /// <summary>
    /// Returns the manifest serialized as JSON suitable for storing in NodePackageVersion.ManifestJson.
    /// </summary>
    public string GenerateManifestJson(ParsedSpec spec)
    {
        var packageId = BuildPackageId(spec.Metadata.Id.Value);
        var manifest = BuildManifestObject(packageId, spec);
        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
    }

    // -------------------------------------------------------------------------
    // PackageId
    // -------------------------------------------------------------------------

    internal static string BuildPackageId(string specId)
    {
        var safe = Regex.Replace(specId.ToLowerInvariant(), @"[^a-z0-9\-]", "");
        if (string.IsNullOrEmpty(safe)) safe = "spec";
        return "openapi." + safe;
    }

    // -------------------------------------------------------------------------
    // Manifest YAML
    // -------------------------------------------------------------------------

    private static string BuildManifestYaml(string packageId, ParsedSpec spec)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"id: {packageId}");
        sb.AppendLine("version: \"1.0\"");
        sb.AppendLine($"displayName: {EscapeYamlScalar(spec.Metadata.Title)}");
        sb.AppendLine("category: Integrations");
        sb.AppendLine("tier: Interpreted");
        sb.AppendLine("sideEffectKind: NonIdempotentSideEffect");
        sb.AppendLine("recoveryMode: RetryAutomatically");
        sb.AppendLine("defaultTimeoutSeconds: 30");
        sb.AppendLine("capabilities:");
        sb.AppendLine("  - http");
        sb.AppendLine("  - credentials");
        sb.AppendLine("parameters:");

        // operationId parameter
        sb.AppendLine("  - name: operationId");
        sb.AppendLine("    type: string");
        sb.AppendLine("    required: true");
        sb.AppendLine("    expression: false");
        if (spec.Operations.Count > 0)
        {
            sb.AppendLine("    values:");
            foreach (var op in spec.Operations)
                sb.AppendLine($"      - {op.OperationId}");
        }
        else
        {
            sb.AppendLine("    values: []");
        }

        // serverConfigId parameter
        sb.AppendLine("  - name: serverConfigId");
        sb.AppendLine("    type: string");
        sb.AppendLine("    required: true");
        sb.AppendLine("    expression: false");

        // specVersion parameter
        sb.AppendLine("  - name: specVersion");
        sb.AppendLine("    type: string");
        sb.AppendLine("    required: false");
        sb.AppendLine("    expression: false");

        // arguments parameter
        sb.AppendLine("  - name: arguments");
        sb.AppendLine("    type: string");
        sb.AppendLine("    required: false");
        sb.AppendLine("    expression: true");

        sb.AppendLine("outputs:");
        sb.AppendLine("  - name: success");
        sb.AppendLine("  - name: error");

        return sb.ToString();
    }

    private static string EscapeYamlScalar(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        // Quote if contains YAML special leading chars or colons
        if (value.IndexOfAny([':', '#', '\'', '"', '{', '}', '[', ']', '&', '*', '!', '|', '>', '%', '@', '`']) >= 0)
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return value;
    }

    // -------------------------------------------------------------------------
    // Manifest object (for JSON serialization to DB)
    // -------------------------------------------------------------------------

    private static object BuildManifestObject(string packageId, ParsedSpec spec)
    {
        var operationIds = spec.Operations.Select(o => o.OperationId).ToList();

        return new
        {
            id = packageId,
            version = "1.0",
            displayName = spec.Metadata.Title,
            category = "Integrations",
            tier = "Interpreted",
            sideEffectKind = "NonIdempotentSideEffect",
            recoveryMode = "RetryAutomatically",
            defaultTimeoutSeconds = 30,
            capabilities = new[] { "http", "credentials" },
            parameters = new object[]
            {
                new { name = "operationId", type = "string", required = true, expression = false, values = operationIds },
                new { name = "serverConfigId", type = "string", required = true, expression = false },
                new { name = "specVersion", type = "string", required = false, expression = false },
                new { name = "arguments", type = "string", required = false, expression = true }
            },
            outputs = new object[]
            {
                new { name = "success" },
                new { name = "error" }
            }
        };
    }
}
