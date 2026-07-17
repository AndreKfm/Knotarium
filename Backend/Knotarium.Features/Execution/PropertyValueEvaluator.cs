// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Execution;

/// <summary>
/// Resolves node property values against the workflow state: unboxes JSON primitives, recurses
/// into structures, looks up <c>variable_ref</c> objects, and evaluates <c>{{ }}</c> expressions.
/// </summary>
internal static class PropertyValueEvaluator
{
    // Resolve a property value against the workflow state. When <paramref name="resolveReferences"/>
    // is false (the param is declared Expression:false in the manifest), references are left intact —
    // variable_ref objects are NOT looked up and "{{ }}" strings are NOT evaluated — but primitives
    // are still unboxed and structures recursed, so a task receives the raw shape it persisted and can
    // resolve its own references with found-ness (D7; e.g. the Condition node).
    public static object? Evaluate(object? value, IWorkflowState state, bool resolveReferences = true)
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
                            return Evaluate(resolved, state, resolveReferences);
                        }
                    }
                }

                var newDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    newDict[property.Name] = Evaluate(property.Value, state, resolveReferences)!;
                }
                return newDict;
            }
            if (element.ValueKind == JsonValueKind.Array)
            {
                var newList = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    newList.Add(Evaluate(item, state, resolveReferences));
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
                        return Evaluate(resolved, state, resolveReferences);
                    }
                }
            }

            var newDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in dict)
            {
                newDict[kvp.Key] = Evaluate(kvp.Value, state, resolveReferences)!;
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
                        return Evaluate(resolved, state, resolveReferences);
                    }
                }
            }

            var newDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in roDict)
            {
                newDict[kvp.Key] = Evaluate(kvp.Value, state, resolveReferences)!;
            }
            return newDict;
        }
        return value;
    }

    // The set of manifest param names declared Expression:false — those skip reference/expression
    // resolution (D7). Built per node from its manifest; empty when there is no manifest.
    public static HashSet<string> NonExpressionParams(NodePackageManifest? manifest)
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
}
