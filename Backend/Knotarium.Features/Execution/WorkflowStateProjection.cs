using System;
using System.Collections.Generic;
using System.Linq;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Features.Execution;

/// <summary>
/// <see cref="IWorkflowState"/> view over a live <see cref="ExecutionInstance"/>: globals (with
/// dotted-path lookup into structured values) plus promoted node-output variables
/// (<c>nodeId_outputHandle</c>).
/// </summary>
/// <remarks>
/// internal (not private) so the D7 resolution-parity test can assert TryResolveVariable agrees
/// with the generic GetVariable&lt;object&gt; path on the very same projection (R3).
/// </remarks>
internal class WorkflowStateProjection : IWorkflowState
{
    private readonly ExecutionInstance _instance;

    public WorkflowStateProjection(ExecutionInstance instance)
    {
        _instance = instance;
    }

    public T? GetVariable<T>(string name)
    {
        // 1. Try direct lookup in GlobalVariables
        if (_instance.GlobalVariables.TryGetValue(name, out var val))
        {
            return ConvertValue<T>(val);
        }

        // 1b. Dotted path into a global object (e.g. "signal.params.valueA") — used by dragged
        // variable refs that target a field of a structured global like the inbound signal.
        if (TryResolveDottedPath(name, out var pathVal))
        {
            return ConvertValue<T>(pathVal);
        }

        // 2. Parse promoted variable pattern (nodeId_outputHandle)
        var lastUnderscore = name.LastIndexOf('_');
        if (lastUnderscore > 0)
        {
            var nodeIdStr = name.Substring(0, lastUnderscore);
            var outputName = name.Substring(lastUnderscore + 1);

            var nodeState = _instance.NodeStates.FirstOrDefault(ns =>
                string.Equals(ns.NodeId.Value, nodeIdStr, StringComparison.OrdinalIgnoreCase));

            if (nodeState != null && nodeState.Outputs.TryGetValue(outputName, out var outputVal))
            {
                return ConvertValue<T>(outputVal);
            }
        }

        return default;
    }

    private static T? ConvertValue<T>(object? val)
    {
        if (val is T typedVal)
            return typedVal;

        if (val is System.Text.Json.JsonElement element)
        {
            try
            {
                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return System.Text.Json.JsonSerializer.Deserialize<T>(element.GetRawText(), options);
            }
            catch
            {
                return default;
            }
        }

        try
        {
            var converted = Convert.ChangeType(val, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            return converted != null ? (T)converted : default;
        }
        catch
        {
            return default;
        }
    }

    public bool TryResolveVariable(string name, out object? value)
    {
        // 1. Direct global (may legitimately be stored as null elsewhere; presence is what matters).
        if (_instance.GlobalVariables.TryGetValue(name, out var val))
        {
            value = val;
            return true;
        }

        // 1b. Dotted path into a structured global (e.g. "signal.params.valueA").
        if (TryResolveDottedPath(name, out value))
        {
            return true;
        }

        // 2. Promoted node-output pattern (nodeId_outputHandle).
        var lastUnderscore = name.LastIndexOf('_');
        if (lastUnderscore > 0)
        {
            var nodeIdStr = name.Substring(0, lastUnderscore);
            var outputName = name.Substring(lastUnderscore + 1);
            var nodeState = _instance.NodeStates.FirstOrDefault(ns =>
                string.Equals(ns.NodeId.Value, nodeIdStr, StringComparison.OrdinalIgnoreCase));
            if (nodeState != null && nodeState.Outputs.TryGetValue(outputName, out var outputVal))
            {
                value = outputVal;
                return true;
            }
        }

        value = null;
        return false; // genuinely missing — distinct from a resolved null.
    }

    // Resolve "head.seg.seg" by looking up the head global, then walking the remaining dotted
    // segments into its value (a JsonElement object after DB round-trip, or a dictionary in memory).
    private bool TryResolveDottedPath(string name, out object? value)
    {
        value = null;
        var dot = name.IndexOf('.');
        if (dot <= 0)
        {
            return false;
        }
        var head = name.Substring(0, dot);
        if (!_instance.GlobalVariables.TryGetValue(head, out var current) || current is null)
        {
            return false;
        }

        foreach (var segment in name.Substring(dot + 1).Split('.'))
        {
            if (segment.Length == 0 || current is null)
            {
                return false;
            }

            if (current is System.Text.Json.JsonElement je)
            {
                if (je.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    return false;
                }
                object? next = null;
                var found = false;
                foreach (var prop in je.EnumerateObject())
                {
                    if (string.Equals(prop.Name, segment, StringComparison.OrdinalIgnoreCase))
                    {
                        next = prop.Value;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    return false;
                }
                current = next;
            }
            else if (current is System.Collections.IDictionary dict)
            {
                object? next = null;
                var found = false;
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    if (entry.Key is string key && string.Equals(key, segment, StringComparison.OrdinalIgnoreCase))
                    {
                        next = entry.Value;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    return false;
                }
                current = next;
            }
            else
            {
                return false;
            }
        }

        value = current;
        return true;
    }

    public void SetVariable(string name, object? value)
    {
        if (value == null)
            _instance.GlobalVariables.Remove(name);
        else
            _instance.GlobalVariables[name] = value;
    }

    public System.Text.Json.JsonElement? GetNodeOutput(NodeId nodeId, string outputName)
    {
        var nodeState = _instance.NodeStates.FirstOrDefault(ns => ns.NodeId == nodeId);
        if (nodeState != null && nodeState.Outputs.TryGetValue(outputName, out var val))
        {
            if (val is System.Text.Json.JsonElement je)
                return je;

            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(val);
                return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }
}
