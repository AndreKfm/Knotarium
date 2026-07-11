using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Features.Portability;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Ai;

/// <summary>The workflow after credential normalization, plus the slot keys left for the user to bind.</summary>
public sealed record GeneratedCredentialFinalizeResult(
    WorkflowDefinition Workflow,
    IReadOnlyList<string> OpenSlots);

/// <summary>
/// Safety net applied to a generated workflow: every <c>credentialRef</c> parameter the model set must be a
/// valid <c>slot:&lt;key&gt;</c> placeholder (the model is never given real credential ids, so any concrete
/// value it produced is fabricated). Non-slot values are rewritten to freshly-allocated slot keys using the
/// canonical grammar (<see cref="CredentialSlotModule.AllocateSlotKey"/>), and the resulting set of unbound
/// slots is reported so the UI can prompt the user to bind them. Reuses the existing portability primitive
/// rather than inventing a new one (settled decision).
///
/// Geometry is intentionally untouched here — layout runs client-side (the canvas Tidy on preview-load).
/// </summary>
public sealed class GeneratedCredentialFinalizer
{
    private const string CredentialRefType = "credentialRef";

    private readonly INodePackageManifestProvider _manifests;

    public GeneratedCredentialFinalizer(INodePackageManifestProvider manifests) => _manifests = manifests;

    public async Task<GeneratedCredentialFinalizeResult> FinalizeAsync(
        WorkflowDefinition workflow,
        CancellationToken cancellationToken = default)
    {
        // Seed the used-set with slot keys already present so we never reallocate over a valid one.
        var used = new HashSet<string>(
            CredentialSlotModule.FindUnboundSlots(workflow.Nodes),
            StringComparer.OrdinalIgnoreCase);

        var newNodes = new List<NodeDefinition>(workflow.Nodes.Count);
        foreach (var node in workflow.Nodes)
        {
            var manifest = await _manifests.GetManifestAsync(new NodePackageId(node.Type), cancellationToken);
            var credentialParams = manifest?.Parameters
                .Where(p => string.Equals(p.Type, CredentialRefType, StringComparison.Ordinal))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            if (credentialParams is null || credentialParams.Count == 0)
            {
                newNodes.Add(node);
                continue;
            }

            Dictionary<string, object>? rewritten = null;
            foreach (var paramName in credentialParams)
            {
                if (!node.Properties.TryGetValue(paramName, out var raw)) continue;
                var current = AsString(raw);
                if (string.IsNullOrWhiteSpace(current)) continue;
                if (CredentialSlotModule.IsCredentialSlotToken(current)) continue;

                // A fabricated, non-slot value — normalize it to a fresh slot derived from the value.
                var slotKey = CredentialSlotModule.AllocateSlotKey(current, used);
                rewritten ??= new Dictionary<string, object>(node.Properties);
                rewritten[paramName] = CredentialSlotTokens.SlotPrefix + slotKey;
            }

            newNodes.Add(rewritten is null ? node : node with { Properties = rewritten });
        }

        var finalWorkflow = workflow with { Nodes = newNodes };
        var openSlots = CredentialSlotModule.FindUnboundSlots(finalWorkflow.Nodes);
        return new GeneratedCredentialFinalizeResult(finalWorkflow, openSlots);
    }

    private static string? AsString(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        _ => null
    };
}
