using System.Collections.Generic;
using System.Text.Json;

namespace Knotarium.Features.Reactive;

/// <summary>
/// Shared reference handling for reactive wires. A reference spec is a
/// <c>{ __type: "variable_ref", variableName }</c> object or a plain variable-name string; a
/// <c>{{ }}</c> expression string is unsupported on the wire (its <c>$node</c>/<c>$variables</c>
/// vocabulary has no meaning for a standing rule) and reads as "no name".
/// </summary>
internal static class ReactiveRefs
{
    public static string? ReadVariableName(object? refSpec)
    {
        switch (refSpec)
        {
            case JsonElement je when je.ValueKind == JsonValueKind.Object:
                return je.TryGetProperty("variableName", out var n) && n.ValueKind == JsonValueKind.String
                    ? NullIfEmptyOrExpression(n.GetString())
                    : null;
            case JsonElement je when je.ValueKind == JsonValueKind.String:
                return NullIfEmptyOrExpression(je.GetString());
            case string s:
                return NullIfEmptyOrExpression(s);
            case IReadOnlyDictionary<string, object> dict:
                return dict.TryGetValue("variableName", out var v) ? NullIfEmptyOrExpression(v?.ToString()) : null;
            default:
                return null;
        }
    }

    /// <summary>True when the value is a variable_ref object (a reference, not a literal).</summary>
    public static bool IsVariableRef(object? value)
    {
        switch (value)
        {
            case JsonElement je when je.ValueKind == JsonValueKind.Object:
                return je.TryGetProperty("__type", out var t) && t.ValueKind == JsonValueKind.String
                    && t.GetString() == "variable_ref";
            case IReadOnlyDictionary<string, object> dict:
                return dict.TryGetValue("__type", out var dt) && dt?.ToString() == "variable_ref";
            default:
                return false;
        }
    }

    private static string? NullIfEmptyOrExpression(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s.Contains("{{") && s.Contains("}}")) return null; // expression → unsupported here
        return s;
    }
}
