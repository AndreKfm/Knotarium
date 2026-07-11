using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Contracts.Ai;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Compiler;

namespace KnotGarden.Features.Ai;

/// <summary>
/// The terminal outcome of the generate→compile→repair loop. On success <see cref="Workflow"/> is a
/// workflow that passed compilation (topology only — geometry is assigned later, in Phase 4).
/// <see cref="Diagnostics"/> holds the final unresolved errors when the loop gives up.
/// </summary>
public sealed record WorkflowGenerationOutcome(
    WorkflowDefinition? Workflow,
    bool Succeeded,
    int Attempts,
    IReadOnlyList<string> Diagnostics,
    string? LastRawText);

/// <summary>
/// Drives a candidate from the model through the in-process <see cref="WorkflowCompiler"/> and, on a
/// parse-or-compile failure, re-invokes the generator with the exact prior errors threaded back — up to
/// <c>MaxRepairAttempts</c> passes. This is what makes generation robust: the model never has to be right
/// first try, and each repair prompt carries the specific <c>ERR_*</c> codes to fix rather than asking it
/// to regenerate blind.
/// </summary>
public sealed class WorkflowGenerationOrchestrator
{
    private readonly IWorkflowGenerator _generator;
    private readonly WorkflowCompiler _compiler;
    private readonly AiGenerationOptions _options;

    public WorkflowGenerationOrchestrator(
        IWorkflowGenerator generator,
        WorkflowCompiler compiler,
        AiGenerationOptions options)
    {
        _generator = generator;
        _compiler = compiler;
        _options = options;
    }

    public async Task<WorkflowGenerationOutcome> GenerateAsync(
        string intent,
        IReadOnlyList<NodePackageManifest> catalog,
        CancellationToken cancellationToken = default,
        WorkflowDefinition? currentWorkflow = null)
    {
        var maxAttempts = Math.Max(1, _options.MaxRepairAttempts);
        IReadOnlyList<string>? priorErrors = null;
        string? lastRawText = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var generated = await _generator.GenerateAsync(
                new WorkflowGenerationRequest(intent, catalog, priorErrors, currentWorkflow), cancellationToken);
            lastRawText = generated.RawText;

            if (!generated.Parsed || generated.Workflow is null)
            {
                // Couldn't even parse the model's output — feed the reason back and try again.
                priorErrors = new[] { generated.ParseError ?? "Model output could not be parsed into a workflow." };
                continue;
            }

            var compilation = await _compiler.CompileAsync(generated.Workflow, cancellationToken);
            if (compilation.IsSuccess)
            {
                return new WorkflowGenerationOutcome(generated.Workflow, true, attempt, Array.Empty<string>(), lastRawText);
            }

            priorErrors = compilation.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(FormatDiagnostic)
                .ToList();
        }

        return new WorkflowGenerationOutcome(
            null, false, maxAttempts, priorErrors ?? Array.Empty<string>(), lastRawText);
    }

    private static string FormatDiagnostic(CompilationDiagnostic d)
    {
        // Keep the ERR_* code — the repair prompt leans on it to target the specific failure.
        var where = d.NodeId is { } nodeId ? $" (node '{nodeId.Value}')"
            : d.EdgeId is { } edgeId ? $" (edge '{edgeId}')"
            : string.Empty;
        return $"{d.Code}: {d.Message}{where}";
    }
}
