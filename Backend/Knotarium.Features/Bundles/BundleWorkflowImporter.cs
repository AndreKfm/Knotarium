// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;

using Knotarium.Features.Execution;
using Knotarium.Features.Portability;

namespace Knotarium.Features.Bundles;

// ─────────────────────────────────────────────────────────────────────────────
// Workflow-import seam — the one piece of install that reaches into the workflow
// versioning stack. It exists as an interface so BundleInstallService can be unit-
// tested with a fake, instead of standing up WorkflowPublisher's full dependency
// graph (compiler, schedule/polling synchronizers, activation services). The real
// implementation is a thin adapter over WorkflowPublisher.ImportAsync, which creates
// an inactive Imported version — install never auto-activates what it brings in.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Imports a bundled workflow document as a new inactive version, returning its version number.</summary>
public interface IBundleWorkflowImporter
{
    Task<int> ImportAsync(WorkflowExportDocument document, CancellationToken cancellationToken = default);
}

/// <summary>Adapts <see cref="WorkflowPublisher"/> to <see cref="IBundleWorkflowImporter"/>.</summary>
public sealed class WorkflowPublisherBundleImporter(WorkflowPublisher publisher) : IBundleWorkflowImporter
{
    public async Task<int> ImportAsync(WorkflowExportDocument document, CancellationToken cancellationToken = default)
    {
        var result = await publisher.ImportAsync(document, cancellationToken).ConfigureAwait(false);
        return result.Version.VersionNumber;
    }
}
