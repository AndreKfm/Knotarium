using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Portability;

/// <summary>
/// Manifest describing an exported workflow version. The <see cref="Checksum"/> is computed over the
/// canonical content (nodes + edges) only, so drift can be detected on import.
/// </summary>
public sealed record WorkflowExportManifest(
    string WorkflowId,
    string WorkflowName,
    int VersionNumber,
    string Origin,
    string? Label,
    string Checksum);

/// <summary>The behavioral content of an exported version.</summary>
public sealed record WorkflowExportContent(
    IReadOnlyList<NodeDefinition> Nodes,
    IReadOnlyList<EdgeDefinition> Edges);

/// <summary>The on-disk shape of an exported workflow version: manifest + content.</summary>
public sealed record WorkflowExportDocument(
    WorkflowExportManifest Manifest,
    WorkflowExportContent Content);

/// <summary>
/// Produces a <strong>deterministic</strong>, secret-free file representation of a workflow version:
/// canonical (recursively key-sorted) JSON with stable node/edge ordering, plus a manifest carrying a
/// content checksum. Determinism is the whole point — without it the git/diff story collapses into noise.
/// </summary>
/// <remarks>
/// Secrets never appear: node properties hold credential/variable <em>references</em> only; encrypted
/// values live in the database and are resolved at runtime. Layout (positions/dimensions) is not yet
/// split into a separate section — a planned refinement; canonical ordering already keeps diffs stable.
/// </remarks>
public static class WorkflowVersionSerializer
{
    private static readonly JsonSerializerOptions ContentOptions = new(PersistenceJsonOptions.Default);
    private static readonly JsonSerializerOptions CanonicalWriteOptions = new() { WriteIndented = true };

    /// <summary>Serializes a version to its canonical on-disk JSON string.</summary>
    public static string Serialize(WorkflowVersion version, string workflowName)
    {
        ArgumentNullException.ThrowIfNull(version);

        var content = BuildOrderedContent(version.Nodes, version.Edges);
        var checksum = ComputeChecksum(content);
        var manifest = new WorkflowExportManifest(
            version.WorkflowDefinitionId.Value,
            workflowName,
            version.VersionNumber,
            version.Origin.ToString(),
            version.Label,
            checksum);

        return Canonicalize(new WorkflowExportDocument(manifest, content));
    }

    /// <summary>
    /// Serializes an already-built document to its canonical on-disk JSON. Used when a document is
    /// transformed in memory (e.g. credential portabilization) and must be written back deterministically.
    /// </summary>
    public static string Serialize(WorkflowExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var content = BuildOrderedContent(document.Content.Nodes, document.Content.Edges);
        return Canonicalize(new WorkflowExportDocument(document.Manifest, content));
    }

    /// <summary>Parses an import file into a document. Throws when the file is empty or malformed.</summary>
    public static WorkflowExportDocument Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<WorkflowExportDocument>(json, ContentOptions);
        if (document is null || document.Manifest is null || document.Content is null)
        {
            throw new InvalidOperationException("The import file is empty or not a valid workflow export.");
        }

        return document;
    }

    /// <summary>Computes the content checksum the manifest carries (sha256 of canonical content).</summary>
    public static string ComputeChecksum(WorkflowExportContent content)
    {
        var canonical = Canonicalize(BuildOrderedContent(content.Nodes, content.Edges));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static WorkflowExportContent BuildOrderedContent(
        IReadOnlyList<NodeDefinition> nodes,
        IReadOnlyList<EdgeDefinition> edges)
    {
        // Stable, content-addressable ordering so the same graph always serializes identically.
        var orderedNodes = nodes
            .OrderBy(node => node.Id.Value, StringComparer.Ordinal)
            .ToList();

        var orderedEdges = edges
            .OrderBy(edge => edge.From.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.Output, StringComparer.Ordinal)
            .ThenBy(edge => edge.To.Value, StringComparer.Ordinal)
            .ThenBy(edge => edge.Input, StringComparer.Ordinal)
            .ThenBy(edge => edge.Id, StringComparer.Ordinal)
            .ToList();

        return new WorkflowExportContent(orderedNodes, orderedEdges);
    }

    private static string Canonicalize(object value)
    {
        var json = JsonSerializer.Serialize(value, ContentOptions);
        using var document = JsonDocument.Parse(json);
        return CanonicalizeElement(document.RootElement)!.ToJsonString(CanonicalWriteOptions);
    }

    private static JsonNode? CanonicalizeElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    obj[property.Name] = CanonicalizeElement(property.Value);
                }

                return obj;

            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(CanonicalizeElement(item));
                }

                return array;

            default:
                return JsonNode.Parse(element.GetRawText());
        }
    }
}
