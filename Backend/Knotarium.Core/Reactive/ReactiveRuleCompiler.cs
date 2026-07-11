using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Reactive;

/// <summary>
/// Compiles a workflow's device-block wiring into a set of standing <see cref="ReactiveRule"/>s.
/// Generic/firewall-clean: it knows only the generic <c>externalDevice</c> node type, the pin handle
/// convention (<c>evt:&lt;type&gt;</c> event-output, <c>act:&lt;type&gt;</c> action-input), the generic
/// <c>condition</c> node shape (<c>in</c> input, <c>true</c>/<c>false</c> outputs) and the generic
/// <c>setVariable</c>/<c>setVariables</c> transform nodes. It never names a provider.
///
/// A wire from an event-output pin reaches one or more action-input pins, optionally routed through
/// Condition nodes (each followed branch adds a guard the inbound signal must clear) and Set Variable(s)
/// nodes (each adds an assignment step applied before later guards read it). The captured steps are kept
/// in path order. Inline Code on the wire is a later phase: a path that hits one stops there.
/// </summary>
public static class ReactiveRuleCompiler
{
    public const string ExternalDeviceNodeType = "externalDevice";
    public const string ConditionNodeType = "condition";
    public const string SetVariableNodeType = "setVariable";
    public const string SetVariablesNodeType = "setVariables";
    public const string EventPinPrefix = "evt:";
    public const string ActionPinPrefix = "act:";
    public const string ConditionInputHandle = "in";
    public const string ConditionTrueOutput = "true";
    public const string ConditionFalseOutput = "false";

    private sealed record Reached(ReactiveSignalRef Trigger, IReadOnlyList<ReactiveStep> Steps, ReactiveSignalRef Effect);

    public static IReadOnlyList<ReactiveRule> Compile(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var nodesById = workflow.Nodes.ToDictionary(n => n.Id.Value, StringComparer.Ordinal);
        var hasDevice = workflow.Nodes.Any(n => n.Type.Equals(ExternalDeviceNodeType, StringComparison.OrdinalIgnoreCase));
        if (!hasDevice)
        {
            return Array.Empty<ReactiveRule>();
        }

        var edgesByFrom = workflow.Edges
            .GroupBy(e => e.From.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var reached = new List<Reached>();

        foreach (var device in workflow.Nodes.Where(IsDevice))
        {
            var triggerTarget = ReadTargetId(device);
            if (string.IsNullOrWhiteSpace(triggerTarget))
            {
                continue;
            }
            if (!edgesByFrom.TryGetValue(device.Id.Value, out var outgoing))
            {
                continue;
            }

            foreach (var edge in outgoing.Where(e => IsEventPin(e.Output)))
            {
                var eventType = edge.Output[EventPinPrefix.Length..];
                if (string.IsNullOrWhiteSpace(eventType))
                {
                    continue;
                }
                var trigger = new ReactiveSignalRef(triggerTarget, eventType);
                // visited tracks only nodes traversed *through* (Condition / Set Variable nodes) to break
                // cycles; the source device is intentionally not seeded so a same-device event→action wire
                // resolves.
                Walk(edge.To.Value, edge.Input, trigger, new List<ReactiveStep>(),
                    new HashSet<string>(StringComparer.Ordinal),
                    nodesById, edgesByFrom, reached);
            }
        }

        return GroupIntoRules(reached);
    }

    /// <summary>
    /// Compile the device-block inbound SIGNAL pins that feed the IMPERATIVE run engine: an event-output
    /// pin OR an incoming-action-output pin wired directly to a normal (non-device) node. Each becomes a
    /// <see cref="ReactiveSignalTrigger"/> that starts a run seeded at the pin's downstream node, so an
    /// inbound device signal can drive ordinary nodes (Log, notifications, …). The device block is a pure
    /// inbound surface: both event and action pins are sources (you react to them); sending a command is
    /// the separate Fire Action node, never an event→action wire. Event pins already consumed by a
    /// <see cref="ReactiveRule"/> (legacy device→device wiring) are excluded so the two never overlap.
    /// </summary>
    public static IReadOnlyList<ReactiveSignalTrigger> CompileSignalTriggers(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        if (!workflow.Nodes.Any(IsDevice))
        {
            return Array.Empty<ReactiveSignalTrigger>();
        }

        var nodesById = workflow.Nodes.ToDictionary(n => n.Id.Value, StringComparer.Ordinal);
        var edgesByFrom = workflow.Edges
            .GroupBy(e => e.From.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Event pins whose wire reaches a device action are legacy reactive rules — exclude them so a pin
        // that feeds both a device action and a normal node doesn't double-fire.
        var reactiveKeys = new HashSet<(string TargetId, string SignalType)>();
        foreach (var rule in Compile(workflow))
        {
            reactiveKeys.Add((rule.Trigger.TargetId, rule.Trigger.SignalType));
        }

        var triggers = new List<ReactiveSignalTrigger>();
        foreach (var device in workflow.Nodes.Where(IsDevice))
        {
            var targetId = ReadTargetId(device);
            if (string.IsNullOrWhiteSpace(targetId)
                || !edgesByFrom.TryGetValue(device.Id.Value, out var outgoing))
            {
                continue;
            }

            foreach (var edge in outgoing)
            {
                ExternalSignalKind kind;
                string pinValue;
                if (IsEventPin(edge.Output))
                {
                    kind = ExternalSignalKind.Event;
                    pinValue = edge.Output[EventPinPrefix.Length..];
                }
                else if (IsActionPin(edge.Output))
                {
                    kind = ExternalSignalKind.Action;
                    pinValue = edge.Output[ActionPinPrefix.Length..];
                }
                else
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(pinValue)
                    || (kind == ExternalSignalKind.Event && reactiveKeys.Contains((targetId!, pinValue)))
                    || !nodesById.TryGetValue(edge.To.Value, out var target)
                    || IsDevice(target))
                {
                    continue;
                }
                triggers.Add(new ReactiveSignalTrigger(kind, targetId!, pinValue, edge.To.Value, device.Id.Value));
            }
        }
        return triggers;
    }

    private static void Walk(
        string nodeId, string inputHandle, ReactiveSignalRef trigger,
        List<ReactiveStep> steps, HashSet<string> visited,
        IReadOnlyDictionary<string, NodeDefinition> nodesById,
        IReadOnlyDictionary<string, List<EdgeDefinition>> edgesByFrom,
        List<Reached> reached)
    {
        if (!visited.Add(nodeId) || !nodesById.TryGetValue(nodeId, out var node))
        {
            return;
        }

        // Reached an action-input pin on a device block → an effect (gated by the steps so far).
        if (IsDevice(node) && IsActionPin(inputHandle))
        {
            var effectTarget = ReadTargetId(node);
            var actionType = inputHandle[ActionPinPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(effectTarget) && !string.IsNullOrWhiteSpace(actionType))
            {
                reached.Add(new Reached(trigger, steps.ToList(), new ReactiveSignalRef(effectTarget, actionType)));
            }
            visited.Remove(nodeId);
            return;
        }

        // A Condition node entered by its `in` port forks the path: follow each branch, recording the
        // matching guard.
        if (node.Type.Equals(ConditionNodeType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(inputHandle, ConditionInputHandle, StringComparison.Ordinal)
            && node.Properties != null
            && node.Properties.TryGetValue("logic", out var logic) && logic != null
            && edgesByFrom.TryGetValue(nodeId, out var branches))
        {
            foreach (var branch in branches)
            {
                bool? expectTrue = branch.Output switch
                {
                    ConditionTrueOutput => true,
                    ConditionFalseOutput => false,
                    _ => null,
                };
                if (expectTrue is null)
                {
                    continue;
                }
                steps.Add(new ReactiveGuard(nodeId, expectTrue.Value, logic));
                Walk(branch.To.Value, branch.Input, trigger, steps, visited, nodesById, edgesByFrom, reached);
                steps.RemoveAt(steps.Count - 1);
            }
        }
        // A Set Variable(s) node is a pass-through transform: record its assignments, then continue along
        // every outgoing edge. Inline Code (and any other node) is not understood on the wire → stop.
        else if (IsTransform(node) && edgesByFrom.TryGetValue(nodeId, out var outs))
        {
            steps.Add(new ReactiveTransform(nodeId, ReadAssignments(node)));
            foreach (var outEdge in outs)
            {
                Walk(outEdge.To.Value, outEdge.Input, trigger, steps, visited, nodesById, edgesByFrom, reached);
            }
            steps.RemoveAt(steps.Count - 1);
        }

        visited.Remove(nodeId);
    }

    private static IReadOnlyList<ReactiveRule> GroupIntoRules(List<Reached> reached)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, (ReactiveSignalRef Trigger, IReadOnlyList<ReactiveStep> Steps, List<ReactiveSignalRef> Effects)>(StringComparer.Ordinal);

        foreach (var r in reached)
        {
            var stepKey = StepKey(r.Steps);
            var key = $"{r.Trigger.TargetId}{r.Trigger.SignalType}{stepKey}";
            if (!groups.TryGetValue(key, out var group))
            {
                group = (r.Trigger, r.Steps, new List<ReactiveSignalRef>());
                groups[key] = group;
                order.Add(key);
            }
            if (!group.Effects.Contains(r.Effect))
            {
                group.Effects.Add(r.Effect);
            }
        }

        var rules = new List<ReactiveRule>(order.Count);
        foreach (var key in order)
        {
            var g = groups[key];
            var stepKey = StepKey(g.Steps);
            rules.Add(new ReactiveRule(
                Id: $"{g.Trigger.TargetId}:{g.Trigger.SignalType}{(stepKey.Length == 0 ? "" : "#" + stepKey)}",
                Trigger: g.Trigger,
                Effects: g.Effects,
                Steps: g.Steps));
        }
        return rules;
    }

    private static string StepKey(IReadOnlyList<ReactiveStep> steps) => string.Join("&", steps.Select(s => s switch
    {
        ReactiveGuard g => $"G{g.SourceNodeId}:{(g.ExpectTrue ? "T" : "F")}",
        ReactiveTransform t => $"X{t.SourceNodeId}",
        _ => "?",
    }));

    private static bool IsDevice(NodeDefinition n)
        => n.Type.Equals(ExternalDeviceNodeType, StringComparison.OrdinalIgnoreCase);

    private static bool IsTransform(NodeDefinition n)
        => n.Type.Equals(SetVariableNodeType, StringComparison.OrdinalIgnoreCase)
        || n.Type.Equals(SetVariablesNodeType, StringComparison.OrdinalIgnoreCase);

    private static bool IsEventPin(string? handle)
        => !string.IsNullOrEmpty(handle) && handle.StartsWith(EventPinPrefix, StringComparison.Ordinal);

    private static bool IsActionPin(string? handle)
        => !string.IsNullOrEmpty(handle) && handle.StartsWith(ActionPinPrefix, StringComparison.Ordinal);

    // Read the name→value assignments off a Set Variable(s) node. `setVariables` carries a `variables`
    // array of { name, value }; `setVariable` carries a single { variableName, value }. Values are kept
    // opaque (JsonElement / CLR) and resolved at dispatch.
    private static IReadOnlyList<ReactiveAssignment> ReadAssignments(NodeDefinition node)
    {
        var result = new List<ReactiveAssignment>();
        if (node.Properties == null)
        {
            return result;
        }

        if (node.Type.Equals(SetVariableNodeType, StringComparison.OrdinalIgnoreCase))
        {
            var name = ReadString(node.Properties, "variableName");
            if (!string.IsNullOrWhiteSpace(name))
            {
                node.Properties.TryGetValue("value", out var val);
                result.Add(new ReactiveAssignment(name!, val));
            }
            return result;
        }

        if (!node.Properties.TryGetValue("variables", out var rows) || rows is null)
        {
            return result;
        }

        foreach (var (name, value) in EnumerateRows(rows))
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                result.Add(new ReactiveAssignment(name!, value));
            }
        }
        return result;
    }

    private static IEnumerable<(string? Name, object? Value)> EnumerateRows(object rows)
    {
        switch (rows)
        {
            case JsonElement je when je.ValueKind == JsonValueKind.Array:
                foreach (var item in je.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    object? value = item.TryGetProperty("value", out var v) ? v.Clone() : null;
                    yield return (name, value);
                }
                break;
            case System.Collections.IEnumerable list and not string:
                foreach (var item in list)
                {
                    if (item is IReadOnlyDictionary<string, object> dict)
                    {
                        var name = dict.TryGetValue("name", out var n) ? n?.ToString() : null;
                        dict.TryGetValue("value", out var value);
                        yield return (name, value);
                    }
                }
                break;
        }
    }

    private static string? ReadString(IReadOnlyDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var raw) || raw is null) return null;
        return raw switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => raw.ToString(),
        };
    }

    /// <summary>
    /// Read a device block's picked target id from its <c>targetId</c> property. The resourceLocator
    /// field persists as <c>{ value, label, mode }</c>, but property values reach us as native CLR
    /// strings (tests/persistence) or <see cref="JsonElement"/> (HTTP) — accept every shape and pull
    /// out the stable <c>value</c>.
    /// </summary>
    internal static string? ReadTargetId(NodeDefinition device)
    {
        if (device.Properties == null || !device.Properties.TryGetValue("targetId", out var raw) || raw is null)
        {
            return null;
        }
        return ExtractStableValue(raw);
    }

    private static string? ExtractStableValue(object raw)
    {
        switch (raw)
        {
            case string s:
                return string.IsNullOrWhiteSpace(s) ? null : s;
            case IReadOnlyDictionary<string, object> dict:
                return dict.TryGetValue("value", out var v) && v is not null ? ExtractStableValue(v) : null;
            case JsonElement je:
                switch (je.ValueKind)
                {
                    case JsonValueKind.String:
                        var str = je.GetString();
                        return string.IsNullOrWhiteSpace(str) ? null : str;
                    case JsonValueKind.Object:
                        return je.TryGetProperty("value", out var prop) ? ExtractStableValue(prop) : null;
                    default:
                        return null;
                }
            default:
                var fallback = raw.ToString();
                return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
        }
    }

    /// <summary>
    /// Read a device block's multi-select pin field (<c>eventPins</c>/<c>actionPins</c>) into a
    /// value→label map, so diagnostics can name a pin by its display label ("Event 002") instead of its
    /// opaque type id ("3"). Tolerates the shapes the resourceLocator multi field persists —
    /// <c>DynamicOptionMultiValue { items: [{ value, label }] }</c>, a bare array, or a single entry — in
    /// CLR or <see cref="JsonElement"/> form (mirrors the editor's readDevicePins).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ReadPinLabels(NodeDefinition node, string property)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node.Properties != null && node.Properties.TryGetValue(property, out var raw) && raw is not null)
        {
            CollectPins(raw, map);
        }
        return map;
    }

    private static void CollectPins(object raw, Dictionary<string, string> map)
    {
        switch (raw)
        {
            case string s:
                AddPin(s, null, map);
                break;
            case JsonElement je:
                CollectPinsJson(je, map);
                break;
            // A dict is also IEnumerable<KVP>, so match it before the general sequence case.
            case IReadOnlyDictionary<string, object> dict:
                if (dict.TryGetValue("items", out var items) && items is not null)
                    CollectPins(items, map);
                else
                    AddPin(ExtractStableValue(dict), dict.TryGetValue("label", out var l) ? l as string : null, map);
                break;
            case System.Collections.IEnumerable seq:
                foreach (var item in seq)
                    if (item is not null) CollectPins(item, map);
                break;
        }
    }

    private static void CollectPinsJson(JsonElement je, Dictionary<string, string> map)
    {
        switch (je.ValueKind)
        {
            case JsonValueKind.String:
                AddPin(je.GetString(), null, map);
                break;
            case JsonValueKind.Array:
                foreach (var el in je.EnumerateArray()) CollectPinsJson(el, map);
                break;
            case JsonValueKind.Object:
                if (je.TryGetProperty("items", out var items))
                    CollectPinsJson(items, map);
                else
                    AddPin(
                        je.TryGetProperty("value", out var pv) ? ExtractStableValue(pv) : null,
                        je.TryGetProperty("label", out var pl) && pl.ValueKind == JsonValueKind.String ? pl.GetString() : null,
                        map);
                break;
        }
    }

    private static void AddPin(string? value, string? label, Dictionary<string, string> map)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        map[value!] = string.IsNullOrWhiteSpace(label) ? value! : label!;
    }
}
