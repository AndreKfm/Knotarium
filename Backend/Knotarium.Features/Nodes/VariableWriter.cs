using System.Text.Json;
using Knotarium.Core.Contracts;
using Knotarium.NodeRuntime;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Shared path-aware variable write used by both the Set Variable and Set Variables nodes,
/// so keyed/nested paths behave identically regardless of which node performs the write.
///
/// A bare/malformed name is a flat set (byte-for-byte legacy). A keyed path
/// (myDict["name"], list[0], a.b[2]) materializes the current head value into a mutable
/// tree, deep-sets the leaf (auto-vivifying intermediates, preserving siblings), and
/// re-stores the head as a JsonElement so the read side can navigate it. Structural
/// conflicts throw <see cref="VariableTreeException"/>.
/// </summary>
public static class VariableWriter
{
    public static void Write(VariableBag variables, string name, object? value)
    {
        if (!VariablePath.TryParse(name, out var path) || path!.Segments.Count == 0)
        {
            variables.Set(name, value);
            return;
        }

        var current = variables.Get<object>(path.Head);
        object? root = current is null ? null : VariableTree.ToMutable(ToElement(current));
        var mutated = VariableTree.Set(root, path.Segments, value);
        variables.Set(path.Head, JsonSerializer.SerializeToElement(mutated));
    }

    private static JsonElement ToElement(object value)
        => value is JsonElement je ? je : JsonSerializer.SerializeToElement(value);
}
