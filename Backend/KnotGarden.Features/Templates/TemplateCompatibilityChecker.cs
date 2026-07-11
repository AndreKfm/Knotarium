using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Features.Bundles;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Compiler;

using KnotGarden.Features.Execution;
using KnotGarden.Features.Portability;

namespace KnotGarden.Features.Templates;

/// <summary>
/// Assesses whether a template's workflow can run on this engine. Engine version alone is insufficient
/// (a workflow can reference node types absent here, with no package lock to consult), so this compiles
/// the graph and inspects the diagnostics: unknown node types make it non-runnable; other diagnostics are
/// surfaced as warnings. The <c>minEngineVersion</c> hint is advisory and contributes a warning when this
/// engine is older.
/// </summary>
public sealed class TemplateCompatibilityChecker(WorkflowCompiler compiler)
{
    public async Task<TemplateCompatibility> AssessAsync(
        WorkflowExportDocument document,
        string? minEngineVersion,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var supported = true;

        if (!string.IsNullOrWhiteSpace(minEngineVersion)
            && SemanticVersion.TryParse(minEngineVersion, out var required)
            && SemanticVersion.TryParse(TemplateFormat.EngineVersion, out var current)
            && current.CompareTo(required) < 0)
        {
            supported = false;
            warnings.Add($"Requires engine version {minEngineVersion} or newer; this engine is {TemplateFormat.EngineVersion}.");
        }

        var definition = new WorkflowDefinition(
            new WorkflowDefinitionId(document.Manifest.WorkflowId),
            string.IsNullOrWhiteSpace(document.Manifest.WorkflowName) ? "Template" : document.Manifest.WorkflowName,
            document.Content.Nodes,
            document.Content.Edges);

        var result = await compiler.CompileAsync(definition, cancellationToken).ConfigureAwait(false);
        foreach (var diagnostic in result.Diagnostics)
        {
            // An unknown/unsupported node type cannot be satisfied by binding credentials — it's a real
            // incompatibility that leaves the imported workflow non-runnable.
            if (diagnostic.Code == "ERR_INVALID_NODE_TYPE")
            {
                supported = false;
                warnings.Add(diagnostic.Message);
            }
            else if (diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            {
                // Configuration-shaped diagnostics (e.g. a still-unbound credential slot) — informational only.
                warnings.Add(diagnostic.Message);
            }
        }

        return new TemplateCompatibility(supported, warnings);
    }
}
