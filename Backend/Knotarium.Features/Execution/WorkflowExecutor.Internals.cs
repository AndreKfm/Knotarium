using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Security;

namespace Knotarium.Features.Execution;

public partial class WorkflowExecutor
{
    // Resolve a property value against the workflow state. When <paramref name="resolveReferences"/>
    // is false (the param is declared Expression:false in the manifest), references are left intact —
    // variable_ref objects are NOT looked up and "{{ }}" strings are NOT evaluated — but primitives
    // are still unboxed and structures recursed, so a task receives the raw shape it persisted and can
    // resolve its own references with found-ness (D7; e.g. the Condition node).
    private object? EvaluatePropertyValue(object? value, IWorkflowState state, bool resolveReferences = true)
    {
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (resolveReferences &&
                    element.TryGetProperty("__type", out var typeProp) &&
                    typeProp.ValueKind == JsonValueKind.String &&
                    typeProp.GetString() == "variable_ref")
                {
                    if (element.TryGetProperty("variableName", out var nameProp) &&
                        nameProp.ValueKind == JsonValueKind.String)
                    {
                        var variableName = nameProp.GetString();
                        if (!string.IsNullOrEmpty(variableName))
                        {
                            var resolved = state.GetVariable<object>(variableName);
                            return EvaluatePropertyValue(resolved, state, resolveReferences);
                        }
                    }
                }

                var newDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    newDict[property.Name] = EvaluatePropertyValue(property.Value, state, resolveReferences)!;
                }
                return newDict;
            }
            if (element.ValueKind == JsonValueKind.Array)
            {
                var newList = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    newList.Add(EvaluatePropertyValue(item, state, resolveReferences));
                }
                return newList;
            }
            if (element.ValueKind == JsonValueKind.String)
            {
                var elementStr = element.GetString();
                if (resolveReferences && elementStr != null && elementStr.Contains("{{") && elementStr.Contains("}}"))
                {
                    return Knotarium.NodeRuntime.ExpressionEvaluator.Evaluate(elementStr, state);
                }
                return elementStr;
            }
            if (element.ValueKind == JsonValueKind.Number)
            {
                if (element.TryGetInt64(out var longVal)) return longVal;
                if (element.TryGetDouble(out var doubleVal)) return doubleVal;
            }
            if (element.ValueKind == JsonValueKind.True) return true;
            if (element.ValueKind == JsonValueKind.False) return false;
            if (element.ValueKind == JsonValueKind.Null) return null;
        }

        if (resolveReferences && value is string strVal && strVal.Contains("{{") && strVal.Contains("}}"))
        {
            return Knotarium.NodeRuntime.ExpressionEvaluator.Evaluate(strVal, state);
        }
        if (value is Dictionary<string, object> dict)
        {
            if (resolveReferences &&
                dict.TryGetValue("__type", out var typeVal) && typeVal?.ToString() == "variable_ref")
            {
                if (dict.TryGetValue("variableName", out var nameVal) && nameVal != null)
                {
                    var variableName = nameVal.ToString();
                    if (!string.IsNullOrEmpty(variableName))
                    {
                        var resolved = state.GetVariable<object>(variableName);
                        return EvaluatePropertyValue(resolved, state, resolveReferences);
                    }
                }
            }

            var newDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in dict)
            {
                newDict[kvp.Key] = EvaluatePropertyValue(kvp.Value, state, resolveReferences)!;
            }
            return newDict;
        }
        if (value is IReadOnlyDictionary<string, object> roDict)
        {
            if (resolveReferences &&
                roDict.TryGetValue("__type", out var typeVal) && typeVal?.ToString() == "variable_ref")
            {
                if (roDict.TryGetValue("variableName", out var nameVal) && nameVal != null)
                {
                    var variableName = nameVal.ToString();
                    if (!string.IsNullOrEmpty(variableName))
                    {
                        var resolved = state.GetVariable<object>(variableName);
                        return EvaluatePropertyValue(resolved, state, resolveReferences);
                    }
                }
            }

            var newDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in roDict)
            {
                newDict[kvp.Key] = EvaluatePropertyValue(kvp.Value, state, resolveReferences)!;
            }
            return newDict;
        }
        return value;
    }

    // The set of manifest param names declared Expression:false — those skip reference/expression
    // resolution (D7). Built per node from its manifest; empty when there is no manifest.
    private static HashSet<string> NonExpressionParams(NodePackageManifest? manifest)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifest?.Parameters != null)
        {
            foreach (var p in manifest.Parameters)
            {
                if (!p.Expression)
                {
                    set.Add(p.Name);
                }
            }
        }
        return set;
    }

    // internal (not private) so the D7 resolution-parity test can assert TryResolveVariable agrees
    // with the generic GetVariable<object> path on the very same projection (R3).
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

    /// <summary>
    /// State view for a single <c>parallelForEach</c> iteration. Reads variables/outputs from the
    /// iteration's PRIVATE globals + body outputs first, falling back to the (read-only) pre-loop
    /// instance state for upstream node outputs. <see cref="SetVariable"/> writes only to the private
    /// copy, so concurrent iterations never collide and their writes are intentionally not shared.
    /// </summary>
    private sealed class IterationState : IWorkflowState
    {
        private readonly Dictionary<string, object> _globals;
        private readonly Dictionary<NodeId, Dictionary<string, object>> _localOutputs;
        private readonly ExecutionInstance _instance;

        public IterationState(
            Dictionary<string, object> globals,
            Dictionary<NodeId, Dictionary<string, object>> localOutputs,
            ExecutionInstance instance)
        {
            _globals = globals;
            _localOutputs = localOutputs;
            _instance = instance;
        }

        public T? GetVariable<T>(string name)
        {
            if (_globals.TryGetValue(name, out var val))
            {
                return ConvertValue<T>(val);
            }

            // Promoted variable pattern (nodeId_outputHandle): prefer a body node from this iteration,
            // then fall back to a completed pre-loop node on the shared instance.
            var lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                var nodeIdStr = name.Substring(0, lastUnderscore);
                var outputName = name.Substring(lastUnderscore + 1);

                foreach (var kvp in _localOutputs)
                {
                    if (string.Equals(kvp.Key.Value, nodeIdStr, StringComparison.OrdinalIgnoreCase) &&
                        kvp.Value.TryGetValue(outputName, out var localVal))
                    {
                        return ConvertValue<T>(localVal);
                    }
                }

                var nodeState = _instance.NodeStates.FirstOrDefault(ns =>
                    string.Equals(ns.NodeId.Value, nodeIdStr, StringComparison.OrdinalIgnoreCase));
                if (nodeState != null && nodeState.Outputs.TryGetValue(outputName, out var outputVal))
                {
                    return ConvertValue<T>(outputVal);
                }
            }

            return default;
        }

        public bool TryResolveVariable(string name, out object? value)
        {
            // 1. Iteration-private global.
            if (_globals.TryGetValue(name, out var val))
            {
                value = val;
                return true;
            }

            // 2. Promoted node-output: prefer a body node from this iteration, then a pre-loop node.
            var lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                var nodeIdStr = name.Substring(0, lastUnderscore);
                var outputName = name.Substring(lastUnderscore + 1);

                foreach (var kvp in _localOutputs)
                {
                    if (string.Equals(kvp.Key.Value, nodeIdStr, StringComparison.OrdinalIgnoreCase) &&
                        kvp.Value.TryGetValue(outputName, out var localVal))
                    {
                        value = localVal;
                        return true;
                    }
                }

                var nodeState = _instance.NodeStates.FirstOrDefault(ns =>
                    string.Equals(ns.NodeId.Value, nodeIdStr, StringComparison.OrdinalIgnoreCase));
                if (nodeState != null && nodeState.Outputs.TryGetValue(outputName, out var outputVal))
                {
                    value = outputVal;
                    return true;
                }
            }

            value = null;
            return false;
        }

        public void SetVariable(string name, object? value)
        {
            if (value == null)
            {
                _globals.Remove(name);
            }
            else
            {
                _globals[name] = value;
            }
        }

        public JsonElement? GetNodeOutput(NodeId nodeId, string outputName)
        {
            object? value = null;
            if (_localOutputs.TryGetValue(nodeId, out var outputs) && outputs.TryGetValue(outputName, out var localVal))
            {
                value = localVal;
            }
            else
            {
                var nodeState = _instance.NodeStates.FirstOrDefault(ns => ns.NodeId == nodeId);
                if (nodeState != null && nodeState.Outputs.TryGetValue(outputName, out var outputVal))
                {
                    value = outputVal;
                }
            }

            if (value == null)
            {
                return null;
            }
            if (value is JsonElement element)
            {
                return element;
            }
            try
            {
                var json = JsonSerializer.Serialize(value);
                return JsonSerializer.Deserialize<JsonElement>(json);
            }
            catch
            {
                return null;
            }
        }

        private static T? ConvertValue<T>(object? val)
        {
            if (val is T typedVal)
            {
                return typedVal;
            }
            if (val is JsonElement element)
            {
                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return JsonSerializer.Deserialize<T>(element.GetRawText(), options);
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
    }

    private sealed record ResumeWorkItemPayload(string? NodeId, Guid? WorkflowVersionId, JsonElement Output);

    private sealed record RetryWorkItemPayload(string? NodeId, int AttemptNumber, Guid? WorkflowVersionId);

    private sealed record ManualDecisionWorkItemPayload(string? NodeId, string Decision, string? Reason, string? ExpectedAttemptId, Guid? WorkflowVersionId);

    private enum ManualDecision
    {
        Retry,
        Skip,
        Fail
    }
}
