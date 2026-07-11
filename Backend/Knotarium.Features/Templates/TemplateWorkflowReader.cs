using System;
using System.Text.Json;

using Knotarium.Features.Execution;
using Knotarium.Features.Portability;

namespace Knotarium.Features.Templates;

/// <summary>
/// Parses and verifies the <c>workflow.json</c> carried by a template: bounds JSON depth and graph size,
/// then verifies the content checksum against the template manifest. Parse-and-validate only — it never
/// deserializes into live node types or executes anything.
/// </summary>
internal static class TemplateWorkflowReader
{
    public static WorkflowExportDocument ReadAndVerify(TemplateArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        // Depth guard before the full parse — a deeply nested document is rejected cheaply.
        try
        {
            using var probe = JsonDocument.Parse(
                archive.WorkflowDocumentJson,
                new JsonDocumentOptions { MaxDepth = TemplateFormat.MaxJsonDepth });
        }
        catch (JsonException ex)
        {
            throw new TemplateArchiveException($"workflow.json is invalid or too deeply nested: {ex.Message}");
        }

        WorkflowExportDocument document;
        try
        {
            document = WorkflowVersionSerializer.Deserialize(archive.WorkflowDocumentJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new TemplateArchiveException($"workflow.json is not a valid workflow export: {ex.Message}");
        }

        if (document.Content.Nodes.Count > TemplateFormat.MaxNodeCount)
        {
            throw new TemplateArchiveException(
                $"workflow.json has {document.Content.Nodes.Count} nodes, exceeding the {TemplateFormat.MaxNodeCount} limit.");
        }

        foreach (var node in document.Content.Nodes)
        {
            if (node.Properties.Count > TemplateFormat.MaxPropertyCountPerNode)
            {
                throw new TemplateArchiveException(
                    $"Node '{node.Id.Value}' has {node.Properties.Count} properties, exceeding the {TemplateFormat.MaxPropertyCountPerNode} limit.");
            }
        }

        // Verify the shipped bytes match the declared checksum (the bytes after credential portabilization).
        var actual = WorkflowVersionSerializer.ComputeChecksum(document.Content);
        if (!string.Equals(actual, archive.Manifest.WorkflowChecksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new TemplateArchiveException(
                "The template's workflow checksum does not match its contents; the archive may be corrupt or tampered with.");
        }

        return document;
    }
}
