using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Condition;

namespace KnotGarden.Features.Nodes.Condition;

/// <summary>
/// Publish-time completeness gate for Condition nodes (Phase 4). A condition is publishable when it
/// can produce a definite branch at run time: it has either a valid typed <c>logic</c> graph, or a
/// usable legacy <c>operator</c> (legacy nodes still run — precedence: logic &gt; legacy). A node with
/// neither — or with a <c>logic</c> blob that no longer parses — is <b>incomplete</b> and would
/// evaluate to <c>Incomplete</c>/config-error at run time, so publishing is blocked here (consistent
/// with the unbound-credential-slot gate). Drafts (the <c>/versions</c> save) are intentionally NOT
/// gated — only publish/activate.
/// </summary>
public static class ConditionPublishGate
{
    public const string NodeType = "condition";

    /// <summary>Returns the ids of condition nodes whose configuration is incomplete (publish-blocking).</summary>
    public static IReadOnlyList<string> FindIncompleteConditions(IEnumerable<NodeDefinition> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return nodes
            .Where(n => string.Equals(n.Type, NodeType, StringComparison.OrdinalIgnoreCase))
            .Where(n => !IsComplete(n.Properties))
            .Select(n => n.Id.Value)
            .ToList();
    }

    private static bool IsComplete(IReadOnlyDictionary<string, object> properties)
    {
        // 1. A valid typed logic graph wins (must parse — an empty/garbage blob does not count).
        if (properties.TryGetValue("logic", out var logic) && HasMeaningfulValue(logic))
        {
            return ConditionLogicParser.TryParse(logic, out _, out _);
        }

        // 2. Otherwise a present legacy operator means the node still runs via the legacy path.
        if (properties.TryGetValue("operator", out var op) && HasMeaningfulValue(op))
        {
            return true;
        }

        // 3. Neither configured → incomplete.
        return false;
    }

    // A property value counts as "set" unless it's null, an empty/whitespace string, or an empty
    // collection. Request properties deserialize to JsonElement; tests may pass plain CLR values.
    private static bool HasMeaningfulValue(object? value)
    {
        switch (value)
        {
            case null:
                return false;
            case JsonElement el:
                return el.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => false,
                    JsonValueKind.String => !string.IsNullOrWhiteSpace(el.GetString()),
                    JsonValueKind.Array => el.GetArrayLength() > 0,
                    JsonValueKind.Object => true,
                    _ => true,
                };
            case string s:
                return !string.IsNullOrWhiteSpace(s);
            default:
                return true;
        }
    }
}
