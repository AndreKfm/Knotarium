// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Reactive;

/// <summary>Severity of a reactive-graph diagnostic.</summary>
public enum ReactiveDiagnosticSeverity
{
    /// <summary>Publish-blocking: the graph looks functional but a rule can't compile.</summary>
    Error,
    /// <summary>Non-blocking: a wire that does nothing / an unused pin — surfaced as a hint.</summary>
    Warning,
}

/// <summary>One finding about a device-block graph, addressed to a node where possible.</summary>
public sealed record ReactiveDiagnostic(
    ReactiveDiagnosticSeverity Severity,
    string Code,
    string? NodeId,
    string Message);

/// <summary>
/// Static validation for device-block reactive graphs (generic/firewall-clean). Catches the two
/// failure modes that compile silently into nothing:
///   • <c>DEVICE_NO_TARGET</c> (error) — a device block whose pins are wired but which has no target
///     picked, so every rule touching it is dropped. Publish-blocking, like an incomplete condition.
///   • <c>DEAD_END_WIRE</c> (warning) — an event-output wire whose path never reaches an action pin
///     (it dead-ends at an unsupported node, an unwired condition branch, etc.), so it does nothing.
/// </summary>
public static class ReactiveGraphValidator
{
    public const string DeviceNoTargetCode = "DEVICE_NO_TARGET";
    public const string DeadEndWireCode = "DEAD_END_WIRE";

    public static IReadOnlyList<ReactiveDiagnostic> Validate(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var devices = workflow.Nodes.Where(IsDevice).ToList();
        if (devices.Count == 0)
        {
            return Array.Empty<ReactiveDiagnostic>();
        }

        var nodesById = workflow.Nodes.ToDictionary(n => n.Id.Value, StringComparer.Ordinal);
        var edgesByFrom = workflow.Edges
            .GroupBy(e => e.From.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var deviceIds = devices.Select(d => d.Id.Value).ToHashSet(StringComparer.Ordinal);

        var diagnostics = new List<ReactiveDiagnostic>();

        // DEVICE_NO_TARGET — a device that participates in wiring but has no target picked.
        foreach (var device in devices)
        {
            if (!string.IsNullOrWhiteSpace(ReactiveRuleCompiler.ReadTargetId(device)))
            {
                continue;
            }
            // The device block is a pure inbound surface: event and incoming-action pins are both sources
            // (outputs). A device wired through any such pin but with no target picked can't run.
            bool hasOutgoingSignalWire = edgesByFrom.TryGetValue(device.Id.Value, out var outs)
                && outs.Any(e => IsEventPin(e.Output) || IsActionPin(e.Output));
            if (hasOutgoingSignalWire)
            {
                diagnostics.Add(new ReactiveDiagnostic(
                    ReactiveDiagnosticSeverity.Error, DeviceNoTargetCode, device.Id.Value,
                    "Device block has wired pins but no target selected — its rules cannot run. Pick a target."));
            }
        }

        // DEAD_END_WIRE — an event-output edge whose path never reaches any action pin.
        foreach (var device in devices.Where(d => !string.IsNullOrWhiteSpace(ReactiveRuleCompiler.ReadTargetId(d))))
        {
            if (!edgesByFrom.TryGetValue(device.Id.Value, out var outs))
            {
                continue;
            }
            // Name pins by their display label ("Event 002") rather than the opaque type id ("3").
            var eventLabels = ReactiveRuleCompiler.ReadPinLabels(device, "eventPins");
            foreach (var edge in outs.Where(e => IsEventPin(e.Output)))
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                if (ReachesAction(edge.To.Value, edge.Input, visited, nodesById, edgesByFrom, deviceIds))
                {
                    continue; // reactive: the wire reaches a device action
                }
                // An event pin wired straight to a normal terminal node (Log, HTTP, …) starts an
                // imperative workflow run (the device-event bridge), so it isn't dead. Reactive
                // intermediaries (Condition / Set Variable) that lead nowhere are still flagged.
                if (nodesById.TryGetValue(edge.To.Value, out var directTarget)
                    && !IsDevice(directTarget) && !IsIntermediary(directTarget))
                {
                    continue;
                }

                var eventType = edge.Output[ReactiveRuleCompiler.EventPinPrefix.Length..];
                var label = eventLabels.TryGetValue(eventType, out var name) ? name : eventType;
                diagnostics.Add(new ReactiveDiagnostic(
                    ReactiveDiagnosticSeverity.Warning, DeadEndWireCode, device.Id.Value,
                    $"Event '{label}' is wired but its path reaches nothing that runs — the wire does nothing."));
            }
        }

        return diagnostics;
    }

    /// <summary>Blocking (Error) diagnostics only — for the publish/activate gate.</summary>
    public static IReadOnlyList<ReactiveDiagnostic> FindBlocking(WorkflowDefinition workflow)
        => Validate(workflow).Where(d => d.Severity == ReactiveDiagnosticSeverity.Error).ToList();

    private static bool ReachesAction(
        string nodeId, string inputHandle, HashSet<string> visited,
        IReadOnlyDictionary<string, NodeDefinition> nodesById,
        IReadOnlyDictionary<string, List<EdgeDefinition>> edgesByFrom,
        HashSet<string> deviceIds)
    {
        if (!visited.Add(nodeId) || !nodesById.TryGetValue(nodeId, out var node))
        {
            return false;
        }

        if (deviceIds.Contains(nodeId))
        {
            // An action-input pin on a (targeted) device terminates a live wire.
            var ok = IsActionPin(inputHandle) && !string.IsNullOrWhiteSpace(ReactiveRuleCompiler.ReadTargetId(node));
            visited.Remove(nodeId);
            return ok;
        }

        bool reaches = false;
        bool isCondition = node.Type.Equals(ReactiveRuleCompiler.ConditionNodeType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(inputHandle, ReactiveRuleCompiler.ConditionInputHandle, StringComparison.Ordinal)
            && node.Properties != null && node.Properties.TryGetValue("logic", out var logic) && logic != null;
        bool isTransform = IsTransform(node);

        if ((isCondition || isTransform) && edgesByFrom.TryGetValue(nodeId, out var outs))
        {
            foreach (var outEdge in outs)
            {
                if (isCondition
                    && outEdge.Output != ReactiveRuleCompiler.ConditionTrueOutput
                    && outEdge.Output != ReactiveRuleCompiler.ConditionFalseOutput)
                {
                    continue; // only the gated branches continue a rule
                }
                if (ReachesAction(outEdge.To.Value, outEdge.Input, visited, nodesById, edgesByFrom, deviceIds))
                {
                    reaches = true;
                    break;
                }
            }
        }

        visited.Remove(nodeId);
        return reaches;
    }

    private static bool IsDevice(NodeDefinition n)
        => n.Type.Equals(ReactiveRuleCompiler.ExternalDeviceNodeType, StringComparison.OrdinalIgnoreCase);

    private static bool IsTransform(NodeDefinition n)
        => n.Type.Equals(ReactiveRuleCompiler.SetVariableNodeType, StringComparison.OrdinalIgnoreCase)
        || n.Type.Equals(ReactiveRuleCompiler.SetVariablesNodeType, StringComparison.OrdinalIgnoreCase);

    // Reactive intermediaries gate/transform a wire but do nothing on their own; a wire that only reaches
    // these (without continuing to a device action) is still a dead end.
    private static bool IsIntermediary(NodeDefinition n)
        => IsTransform(n)
        || n.Type.Equals(ReactiveRuleCompiler.ConditionNodeType, StringComparison.OrdinalIgnoreCase);

    private static bool IsEventPin(string? handle)
        => !string.IsNullOrEmpty(handle) && handle.StartsWith(ReactiveRuleCompiler.EventPinPrefix, StringComparison.Ordinal);

    private static bool IsActionPin(string? handle)
        => !string.IsNullOrEmpty(handle) && handle.StartsWith(ReactiveRuleCompiler.ActionPinPrefix, StringComparison.Ordinal);
}
