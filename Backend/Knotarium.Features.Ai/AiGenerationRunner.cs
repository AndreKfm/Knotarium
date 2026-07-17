// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Features.Ai;
using Knotarium.Features.Compiler;

namespace Knotarium.Features.Ai;

/// <summary>The result of running one generation job through the full pipeline (orchestrate + finalize).</summary>
public sealed record AiGenerationRunResult(
    bool Succeeded,
    Knotarium.Core.Domain.WorkflowDefinition? Workflow,
    IReadOnlyList<string> OpenSlots,
    IReadOnlyList<string> Diagnostics,
    int Attempts);

public interface IAiGenerationRunner
{
    Task<AiGenerationRunResult> RunAsync(
        string intent,
        CancellationToken cancellationToken = default,
        Knotarium.Core.Domain.WorkflowDefinition? currentWorkflow = null);
}

/// <summary>
/// Composes the backend pieces into one job run: source the inline catalog (built-ins + every deployed
/// binary/DB node package, so the model can use plugin nodes too — e.g. integration workflow nodes), drive
/// the generate→compile→repair loop, and on success finalize credential slots. Transport/config failures
/// from the generator propagate as exceptions for the worker to record; a give-up returns
/// <c>Succeeded=false</c> with diagnostics.
/// </summary>
public sealed class AiGenerationRunner : IAiGenerationRunner
{
    private readonly INodePackageCatalogProvider _catalogProvider;
    private readonly WorkflowGenerationOrchestrator _orchestrator;
    private readonly GeneratedCredentialFinalizer _finalizer;

    public AiGenerationRunner(
        INodePackageCatalogProvider catalogProvider,
        WorkflowGenerationOrchestrator orchestrator,
        GeneratedCredentialFinalizer finalizer)
    {
        _catalogProvider = catalogProvider;
        _orchestrator = orchestrator;
        _finalizer = finalizer;
    }

    public async Task<AiGenerationRunResult> RunAsync(
        string intent,
        CancellationToken cancellationToken = default,
        Knotarium.Core.Domain.WorkflowDefinition? currentWorkflow = null)
    {
        var catalog = await _catalogProvider.GetAllManifestsAsync(cancellationToken);

        var outcome = await _orchestrator.GenerateAsync(intent, catalog, cancellationToken, currentWorkflow);
        if (!outcome.Succeeded || outcome.Workflow is null)
        {
            return new AiGenerationRunResult(false, null, Array.Empty<string>(), outcome.Diagnostics, outcome.Attempts);
        }

        var finalized = await _finalizer.FinalizeAsync(outcome.Workflow, cancellationToken);
        return new AiGenerationRunResult(true, finalized.Workflow, finalized.OpenSlots, Array.Empty<string>(), outcome.Attempts);
    }
}
