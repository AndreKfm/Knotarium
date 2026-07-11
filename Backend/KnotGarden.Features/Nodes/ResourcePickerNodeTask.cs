using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Contracts.Options;
using KnotGarden.Features.Options;

namespace KnotGarden.Features.Nodes;

/// <summary>
/// A node whose value is chosen from a live resource list at design time (via the dynamic-options
/// picker) and emitted as outputs so it can be promoted to a workflow variable and reused read-only
/// across other nodes — no manual id/name entry.
///
/// At run time it re-resolves the stored stable key against the current list (reusing
/// <see cref="ResourceResolver"/>), so the emitted value/label are fresh and a deleted/renamed
/// resource fails the node closed rather than passing a stale value downstream.
/// </summary>
public sealed class ResourcePickerNodeTask : INodeTask
{
    private const string LoaderName = RestCollectionOptionsLoader.LoaderName;

    private readonly ResourceResolver _resolver;

    public ResourcePickerNodeTask(ResourceResolver resolver) => _resolver = resolver;

    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var connectionId = GetString(context, "serverConfigId");
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return new LegacyNodeResult.Failure("Resource Picker: no server configuration selected.");
        }

        var path = GetString(context, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return new LegacyNodeResult.Failure("Resource Picker: no resource collection path configured.");
        }

        if (!context.Inputs.TryGetValue("selection", out var selection) || selection is null)
        {
            return new LegacyNodeResult.Failure("Resource Picker: nothing selected.");
        }

        // Loader configuration travels through dependsOn, exactly as the design-time picker sends it.
        var dependsOn = new Dictionary<string, string>
        {
            ["path"] = path!,
            ["labelField"] = GetString(context, "labelField") ?? "name",
            ["valueField"] = GetString(context, "valueField") ?? "id",
        };
        var collectionField = GetString(context, "collectionField");
        if (!string.IsNullOrWhiteSpace(collectionField))
        {
            dependsOn["collectionField"] = collectionField!;
        }

        var loadContext = new OptionLoadContext(connectionId, dependsOn);

        try
        {
            var resolution = await _resolver.ResolveAsync(LoaderName, selection, loadContext, cancellationToken);
            if (resolution.Resources.Count == 0)
            {
                return new LegacyNodeResult.Failure("Resource Picker: nothing selected.");
            }

            // Single-select: emit the first resolved resource. value = stable key, label = display,
            // record = both combined into one object for downstream nodes that want the whole pick.
            var picked = resolution.Resources[0];
            var outputs = new Dictionary<string, object>
            {
                ["value"] = picked.Value,
                ["label"] = picked.Label,
                ["record"] = new Dictionary<string, object> { ["value"] = picked.Value, ["label"] = picked.Label },
            };
            return new LegacyNodeResult.Success(outputs);
        }
        catch (ResourceResolutionException ex)
        {
            // Fail-closed: a deleted/renamed/ambiguous selection stops the node with a clear message.
            return new LegacyNodeResult.Failure($"Resource Picker: {ex.Message}");
        }
        catch (OptionsLoadException ex)
        {
            return new LegacyNodeResult.Failure($"Resource Picker: {ex.Message}");
        }
    }

    private static string? GetString(NodeExecutionContext context, string key)
    {
        if (!context.Inputs.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            string s => s,
            JsonElement el => el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText(),
            _ => value.ToString(),
        };
    }
}
