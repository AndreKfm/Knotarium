using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Features.Portability;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;

using KnotGarden.Features.Execution;

namespace KnotGarden.Features.Bundles;

// ─────────────────────────────────────────────────────────────────────────────
// Workflow source (IO edge) — produces the canonical workflow JSON a bundle carries
// under workflows/<ref>. It reuses WorkflowVersionSerializer, the same deterministic,
// secret-free writer the standalone workflow export uses, so a bundled workflow is
// byte-identical to one exported on its own (node/credential references only, never
// secret values).
//
// Convention: BundleWorkflowRef.Key is the WorkflowDefinitionId. The "current
// published state" is exported — the active version, or the latest when none is
// active — mirroring WorkflowExportService so a bundle and a folder export agree.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Raised when a manifest's workflow ref names a workflow that has no exportable version.</summary>
public sealed class BundleWorkflowNotFoundException(string workflowKey)
    : InvalidOperationException($"The bundle references workflow '{workflowKey}', but it has no version to export.")
{
    public string WorkflowKey { get; } = workflowKey;
}

/// <summary>Resolves a bundle's workflow refs to their canonical export-document JSON.</summary>
public interface IBundleWorkflowSource
{
    /// <summary>
    /// Returns the canonical <c>WorkflowExportDocument</c> JSON for <paramref name="workflowRef"/>. Throws
    /// <see cref="BundleWorkflowNotFoundException"/> when the referenced workflow has no version to export.
    /// </summary>
    Task<string> GetWorkflowDocumentAsync(BundleWorkflowRef workflowRef, CancellationToken cancellationToken = default);
}

/// <summary>Workflow source over the shared <see cref="IPublishedWorkflowExportSource"/>, reusing the export serializer.</summary>
public sealed class RegistryBundleWorkflowSource(
    IPublishedWorkflowExportSource publishedWorkflowSource) : IBundleWorkflowSource
{
    public async Task<string> GetWorkflowDocumentAsync(
        BundleWorkflowRef workflowRef,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflowRef);
        var workflowId = new WorkflowDefinitionId(workflowRef.Key);

        var published = await publishedWorkflowSource.GetAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (published is null)
        {
            throw new BundleWorkflowNotFoundException(workflowRef.Key);
        }

        return WorkflowVersionSerializer.Serialize(published.Version, published.DisplayName);
    }
}
