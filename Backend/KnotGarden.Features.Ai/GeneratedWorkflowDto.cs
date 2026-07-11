using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnotGarden.Core.Domain;

namespace KnotGarden.Features.Ai;

/// <summary>
/// The flat JSON shape the model is asked to emit (see <see cref="GenerationPromptBuilder"/>): string
/// ids, no <c>{ "value": … }</c> wrappers, no coordinates. Kept deliberately separate from the domain
/// <see cref="WorkflowDefinition"/> so the model-facing contract can stay simple while the domain shape
/// evolves. <see cref="ToWorkflowDefinition"/> bridges the two and reports a human-readable reason on any
/// structural failure (which becomes a repairable parse error, not an exception).
/// </summary>
public sealed record GeneratedWorkflowDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("nodes")] List<GeneratedNodeDto>? Nodes,
    [property: JsonPropertyName("edges")] List<GeneratedEdgeDto>? Edges);

public sealed record GeneratedNodeDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("properties")] Dictionary<string, JsonElement>? Properties);

public sealed record GeneratedEdgeDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("output")] string? Output,
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("input")] string? Input);

public static class GeneratedWorkflowMapper
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions FlatOptions = new() { WriteIndented = false };

    /// <summary>
    /// Serialize a domain <see cref="WorkflowDefinition"/> into the same flat shape the model emits
    /// (string ids, no <c>{ "value": … }</c> wrappers, no coordinates). Used to show the model an existing
    /// workflow when refining it.
    /// </summary>
    public static string ToFlatJson(WorkflowDefinition workflow)
    {
        var dto = new
        {
            name = workflow.Name,
            nodes = workflow.Nodes.Select(n => new { id = n.Id.Value, type = n.Type, properties = n.Properties }),
            edges = workflow.Edges.Select(e => new { id = e.Id, from = e.From.Value, output = e.Output, to = e.To.Value, input = e.Input }),
        };
        return JsonSerializer.Serialize(dto, FlatOptions);
    }

    /// <summary>
    /// Parse the model's raw text into a <see cref="WorkflowDefinition"/>. Returns the workflow on
    /// success; otherwise <c>null</c> + a reason. Never throws on malformed model output — the reason is
    /// fed back into the repair loop. Defensively strips ```json fences in case the model adds them
    /// despite the instruction not to.
    /// </summary>
    public static (WorkflowDefinition? Workflow, string? Error) TryParse(string rawText)
    {
        var json = StripFences(rawText);
        if (string.IsNullOrWhiteSpace(json))
            return (null, "Model returned no content.");

        GeneratedWorkflowDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<GeneratedWorkflowDto>(json, ParseOptions);
        }
        catch (JsonException ex)
        {
            return (null, $"Output was not valid JSON: {ex.Message}");
        }

        if (dto is null)
            return (null, "Output parsed to null.");
        if (dto.Nodes is null || dto.Nodes.Count == 0)
            return (null, "Workflow has no nodes.");

        var nodes = new List<NodeDefinition>(dto.Nodes.Count);
        foreach (var (n, i) in dto.Nodes.Select((n, i) => (n, i)))
        {
            if (string.IsNullOrWhiteSpace(n.Id))
                return (null, $"Node at index {i} is missing an id.");
            if (string.IsNullOrWhiteSpace(n.Type))
                return (null, $"Node '{n.Id}' is missing a type.");

            var properties = new Dictionary<string, object>(StringComparer.Ordinal);
            if (n.Properties is not null)
            {
                foreach (var kvp in n.Properties)
                {
                    var value = ToClrValue(kvp.Value);
                    if (value is not null) properties[kvp.Key] = value;
                }
            }

            nodes.Add(new NodeDefinition(NodeId.Create(n.Id!), n.Type!, properties));
        }

        var edges = new List<EdgeDefinition>(dto.Edges?.Count ?? 0);
        if (dto.Edges is not null)
        {
            foreach (var (e, i) in dto.Edges.Select((e, i) => (e, i)))
            {
                if (string.IsNullOrWhiteSpace(e.From))
                    return (null, $"Edge at index {i} is missing 'from'.");
                if (string.IsNullOrWhiteSpace(e.To))
                    return (null, $"Edge at index {i} is missing 'to'.");

                edges.Add(new EdgeDefinition(
                    string.IsNullOrWhiteSpace(e.Id) ? $"e{i + 1}" : e.Id!,
                    NodeId.Create(e.From!),
                    // Sensible control-flow defaults when the model omits a port; the compiler still
                    // validates the actual port names and the repair loop fixes genuine mismatches.
                    string.IsNullOrWhiteSpace(e.Output) ? "result" : e.Output!,
                    NodeId.Create(e.To!),
                    string.IsNullOrWhiteSpace(e.Input) ? "in" : e.Input!));
            }
        }

        var workflow = new WorkflowDefinition(
            WorkflowDefinitionId.New(),
            string.IsNullOrWhiteSpace(dto.Name) ? "Generated workflow" : dto.Name!.Trim(),
            nodes,
            edges);

        return (workflow, null);
    }

    private static object? ToClrValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        // Object/array properties (a resourceLocator {value,label,mode} pick, a keyValue/header array, a
        // condition logic graph, a dynamicFields object) are preserved as structured JSON — the same shape
        // a UI-saved workflow carries (Properties is IReadOnlyDictionary<string, object>, where an object
        // value is a JsonElement). Stringifying them here (GetRawText) corrupted list picks into "manually
        // entered" strings and broke the modify-in-place round-trip: ToFlatJson shows the model the real
        // object, the model echoes it, and it must survive re-parse as an object. Clone() detaches the
        // element from the source document so it's safe to retain.
        _ => element.Clone()
    };

    private static string StripFences(string raw)
    {
        var text = raw.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        // Drop the opening fence line (``` or ```json) and the trailing fence.
        var firstNewline = text.IndexOf('\n');
        if (firstNewline >= 0) text = text[(firstNewline + 1)..];
        if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];
        return text.Trim();
    }
}
