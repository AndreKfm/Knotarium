using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Features.Execution;

/// <summary>
/// Decides where a fresh run starts: matches the run's trigger origin (manual / schedule /
/// webhook / poll / error / device-event) against the plan's trigger entry nodes, and produces
/// the synthetic outputs a completed trigger node exposes downstream.
/// </summary>
internal sealed class TriggerEntryResolver
{
    private readonly NodeManifestSource _manifests;

    public TriggerEntryResolver(NodeManifestSource manifests)
    {
        _manifests = manifests;
    }

    public async Task<ImmutableArray<NodeId>> ResolveEntryNodesAsync(
        ExecutionPlan plan,
        ExecutionInstance instance,
        CancellationToken cancellationToken)
    {
        var triggerOrigin = instance.TriggerOrigin;

        // A device-event run carries the explicit entry nodes (the fired event pin's downstream nodes);
        // begin there rather than at a compiled trigger so the device event drives exactly that wire.
        if (triggerOrigin.Equals(ExternalSignalRunEnqueuer.DeviceEventTriggerOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return ResolveDeviceEventEntryNodes(plan, instance);
        }

        var matchingEntryNodes = new List<NodeId>();
        foreach (var entryNodeId in plan.EntryNodes)
        {
            var plannedNode = plan.Nodes.FirstOrDefault(node => node.Id == entryNodeId);
            if (plannedNode is null)
            {
                continue;
            }

            var manifest = await _manifests.GetManifestAsync(plannedNode.Type, cancellationToken);
            if (manifest?.TriggerOnly != true)
            {
                continue;
            }

            if (IsTriggerCompatibleWithOrigin(plannedNode.Type, triggerOrigin))
            {
                matchingEntryNodes.Add(entryNodeId);
            }
        }

        return matchingEntryNodes.Count > 0 ? matchingEntryNodes.ToImmutableArray() : plan.EntryNodes;
    }

    public static Dictionary<string, object> CreateTriggerOutputs(string nodeType, ExecutionInstance instance)
    {
        var outputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (nodeType.Equals("scheduler", StringComparison.OrdinalIgnoreCase))
        {
            outputs["triggeredAt"] = instance.CreatedAt;
        }
        else if (nodeType.Equals("pollingTrigger", StringComparison.OrdinalIgnoreCase))
        {
            if (instance.GlobalVariables is not null &&
                instance.GlobalVariables.TryGetValue(TriggerPayloadKeys.Poll, out var payload) &&
                payload is not null)
            {
                outputs["result"] = payload;
            }
        }
        else if (nodeType.Equals("errorTrigger", StringComparison.OrdinalIgnoreCase))
        {
            if (instance.GlobalVariables is not null)
            {
                // The whole failure context on `result`, plus each field on its own output so it can be
                // promoted to a draggable variable in the editor (resolves via nodeState.Outputs[field]).
                if (instance.GlobalVariables.TryGetValue(TriggerPayloadKeys.Error, out var payload) &&
                    payload is not null)
                {
                    outputs["result"] = payload;
                }

                foreach (var key in ErrorWorkflowRunEnqueuer.FieldKeys)
                {
                    if (instance.GlobalVariables.TryGetValue(key, out var fieldValue) && fieldValue is not null)
                    {
                        outputs[key] = fieldValue;
                    }
                }
            }
        }

        return outputs;
    }

    /// <summary>
    /// Entry nodes for a device-event run: the explicit ids carried in globals (the fired event pin's
    /// downstream nodes), kept only if they exist in the plan. No fallback to <c>plan.EntryNodes</c> —
    /// an empty/stale set must start nothing, not run unrelated triggers.
    /// </summary>
    private static ImmutableArray<NodeId> ResolveDeviceEventEntryNodes(ExecutionPlan plan, ExecutionInstance instance)
    {
        if (instance.GlobalVariables is null
            || !instance.GlobalVariables.TryGetValue(ExternalSignalRunEnqueuer.EntryNodesVariableKey, out var raw)
            || raw is null)
        {
            return ImmutableArray<NodeId>.Empty;
        }

        var planNodeIds = plan.Nodes.Select(n => n.Id.Value).ToHashSet(StringComparer.Ordinal);
        var entryNodes = new List<NodeId>();
        foreach (var id in EnumerateEntryNodeIds(raw))
        {
            if (planNodeIds.Contains(id))
            {
                entryNodes.Add(NodeId.Create(id));
            }
        }
        return entryNodes.ToImmutableArray();
    }

    // Globals round-trip through JSON, so the stored List<string> comes back as a JsonElement array on
    // reload; accept both that and the in-memory list/enumerable shape.
    private static IEnumerable<string> EnumerateEntryNodeIds(object raw)
    {
        switch (raw)
        {
            case JsonElement je when je.ValueKind == JsonValueKind.Array:
                foreach (var el in je.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var s = el.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) yield return s!;
                    }
                }
                break;
            case IEnumerable<string> strings:
                foreach (var s in strings)
                {
                    if (!string.IsNullOrWhiteSpace(s)) yield return s;
                }
                break;
            case System.Collections.IEnumerable seq and not string:
                foreach (var item in seq)
                {
                    var s = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) yield return s!;
                }
                break;
        }
    }

    internal static bool IsTriggerCompatibleWithOrigin(string nodeType, string triggerOrigin)
    {
        if (triggerOrigin.Equals("schedule", StringComparison.OrdinalIgnoreCase))
        {
            return nodeType.Equals("scheduler", StringComparison.OrdinalIgnoreCase);
        }

        if (triggerOrigin.Equals("webhook", StringComparison.OrdinalIgnoreCase))
        {
            return nodeType.Equals("webhookTrigger", StringComparison.OrdinalIgnoreCase);
        }

        if (triggerOrigin.Equals("poll", StringComparison.OrdinalIgnoreCase))
        {
            return nodeType.Equals("pollingTrigger", StringComparison.OrdinalIgnoreCase);
        }

        if (triggerOrigin.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            return nodeType.Equals("errorTrigger", StringComparison.OrdinalIgnoreCase);
        }

        return nodeType.Equals("start", StringComparison.OrdinalIgnoreCase)
            || nodeType.Equals("manualTrigger", StringComparison.OrdinalIgnoreCase);
    }
}
