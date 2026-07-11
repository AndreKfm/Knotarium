using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Ai;

/// <summary>
/// Projects the full node-package catalog into the compact, model-facing description fed to the
/// workflow generator. This is the inline catalog (settled decision: no vector retrieval) — it keeps
/// only what the model needs to <em>select</em> and <em>configure</em> nodes: id, display name,
/// category, trigger-ness, each parameter's name/type/required/enum-values/description, and the named
/// output ports. Execution-only metadata (tier, recovery mode, timeouts, retry, side-effect kind) is
/// dropped — the model never reasons about it and it only burns tokens.
///
/// The output is line-oriented rather than JSON: it is markedly more token-efficient than re-serializing
/// the manifest and just as unambiguous for the model. A structured <see cref="ProjectedNode"/> list is
/// produced first so the projection can be asserted independently of its rendered form.
/// </summary>
public static class CatalogProjection
{
    /// <summary>
    /// Categories excluded from the generation catalog by default:
    /// <list type="bullet">
    /// <item><b>Annotations</b> (sticky notes, groups) are inert editor-only decorations with no ports —
    /// emitting them as workflow logic is pure waste.</item>
    /// <item><b>External</b> devices declare their connectable pins <em>dynamically</em> from config, so
    /// they expose no static ports the model could wire; the model cannot produce a valid externalDevice
    /// graph from the catalog alone.</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultExcludedCategories =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Annotations", "External" };

    /// <summary>
    /// Node ids excluded regardless of category. <c>externalDevice</c> exposes its connectable pins
    /// <em>dynamically</em> from config (no static ports), so the model can't wire it from the catalog —
    /// exclude it by id since a deployed plugin build may re-categorize it out of the "External" category.
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultExcludedIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "externalDevice",
            // actionTrigger is excluded from generation: the model kept substituting it for a device block
            // it dropped, and it isn't a working standalone entry point. Event-driven workflows start from
            // an externalDevice block (preserved on refine), not a generated actionTrigger.
            "actionTrigger",
        };

    public sealed record ProjectedParam(
        string Name,
        string Type,
        bool Required,
        IReadOnlyList<string>? Values,
        string? Description);

    public sealed record ProjectedNode(
        string Id,
        string DisplayName,
        string Category,
        bool TriggerOnly,
        IReadOnlyList<ProjectedParam> Parameters,
        IReadOnlyList<string> Outputs,
        string? Description);

    /// <summary>
    /// Reduce the raw manifests to the model-facing shape, dropping excluded categories and sorting by
    /// id for a stable, diffable prompt.
    /// </summary>
    public static IReadOnlyList<ProjectedNode> Project(
        IEnumerable<NodePackageManifest> manifests,
        IReadOnlySet<string>? excludedCategories = null,
        IReadOnlySet<string>? excludedIds = null)
    {
        var excluded = excludedCategories ?? DefaultExcludedCategories;
        var excludedNodeIds = excludedIds ?? DefaultExcludedIds;

        return manifests
            .Where(m => !excluded.Contains(m.Category) && !excludedNodeIds.Contains(m.Id.Value))
            .OrderBy(m => m.Id.Value, StringComparer.Ordinal)
            .Select(m => new ProjectedNode(
                m.Id.Value,
                m.DisplayName,
                m.Category,
                m.TriggerOnly,
                m.Parameters
                    .Select(p => new ProjectedParam(
                        p.Name,
                        p.Type,
                        p.Required,
                        p.Values is { Count: > 0 } ? p.Values : null,
                        string.IsNullOrWhiteSpace(p.Description) ? null : p.Description))
                    .ToList(),
                m.Outputs.Select(o => o.Name).ToList(),
                string.IsNullOrWhiteSpace(m.Description) ? null : m.Description!.Trim()))
            .ToList();
    }

    /// <summary>Render the projected catalog into the compact, line-oriented form embedded in the prompt.</summary>
    public static string Render(IReadOnlyList<ProjectedNode> nodes)
    {
        var sb = new StringBuilder();
        foreach (var node in nodes)
        {
            sb.Append(node.Id).Append(" (").Append(node.DisplayName).Append(')');
            if (node.TriggerOnly) sb.Append(" [TRIGGER]");
            sb.Append(" — ").Append(node.Category);
            // The description is the key disambiguator when display name + category are ambiguous
            // (e.g. vendor packs where several nodes share a category).
            if (!string.IsNullOrWhiteSpace(node.Description)) sb.Append(": ").Append(node.Description);
            sb.Append('\n');

            if (node.Parameters.Count > 0)
            {
                sb.Append("  params: ");
                sb.Append(string.Join(", ", node.Parameters.Select(RenderParam)));
                sb.Append('\n');
            }

            sb.Append("  outputs: ");
            sb.Append(node.Outputs.Count > 0 ? string.Join("|", node.Outputs) : "(none)");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Convenience: project then render in one call.</summary>
    public static string ProjectAndRender(
        IEnumerable<NodePackageManifest> manifests,
        IReadOnlySet<string>? excludedCategories = null,
        IReadOnlySet<string>? excludedIds = null)
        => Render(Project(manifests, excludedCategories, excludedIds));

    private static string RenderParam(ProjectedParam p)
    {
        // name:type, with `!` marking required and enum values inlined as type(a|b|c). A description,
        // when present, follows in quotes.
        var type = p.Values is { Count: > 0 }
            ? $"{p.Type}({string.Join("|", p.Values)})"
            : p.Type;
        var head = $"{p.Name}:{type}{(p.Required ? "!" : string.Empty)}";
        return p.Description is null ? head : $"{head} \"{p.Description}\"";
    }
}
