using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Features.Bundles;

// ─────────────────────────────────────────────────────────────────────────────
// Export orchestration — the first thing that produces a real, downloadable
// .kgbundle. It ties the whole stack together end to end:
//
//   manifest ─► IBundlePackageSource ─► SelectBest ─► Resolve ─► lock
//            └► IBundleWorkflowSource ─► workflow docs
//            └► package + workflow + lock ─► BundleArchiveCodec.Write ─► bytes
//
// Orchestration only: every decision (which version, hash, trust, archive layout)
// lives in the pure components it calls. The two IO seams are injected, so this is
// unit-testable with in-memory sources and has no direct DB/disk dependency.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Inputs for an export run: the authored manifest plus the host's verification context.</summary>
/// <param name="Manifest">The authoring-intent <c>bundle.json</c> to resolve and pack.</param>
/// <param name="TrustedPublicKeysBase64">Keys used to derive each package's trust level in the lock.</param>
/// <param name="ResolverVersion">Stamped into the lock for provenance/repro.</param>
public sealed record BundleExportInput(
    BundleManifest Manifest,
    IReadOnlyList<string> TrustedPublicKeysBase64,
    string ResolverVersion);

/// <summary>Builds a complete <c>.kgbundle</c> byte array from an authored manifest.</summary>
public sealed class BundleExportService(
    IBundlePackageSource packageSource,
    IBundleWorkflowSource workflowSource,
    TimeProvider timeProvider)
{
    /// <summary>The resolver version stamped into the lock when the caller doesn't supply one.</summary>
    public const string DefaultResolverVersion = "1.0.0";

    /// <summary>
    /// Resolves <paramref name="input"/>'s manifest against the live registry, locks it, gathers each
    /// referenced workflow, and serializes the lot into a single archive. Propagates
    /// <see cref="BundlePackageNotFoundException"/> when a ref can't be satisfied and
    /// <see cref="BundleWorkflowNotFoundException"/> when a workflow has nothing to export — export fails
    /// loud rather than emitting a partial bundle.
    /// </summary>
    public async Task<byte[]> ExportAsync(BundleExportInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Manifest);
        var manifest = input.Manifest;

        // 1. Resolve the manifest's package refs against the installed registry, then lock.
        var available = await packageSource
            .GetAvailableAsync(manifest.Packages.Select(packageRef => packageRef.Id), cancellationToken)
            .ConfigureAwait(false);
        var resolved = BundlePackageResolver.SelectBest(manifest.Packages, available);
        var @lock = BundleResolver.Resolve(
            manifest,
            resolved,
            input.TrustedPublicKeysBase64 ?? [],
            string.IsNullOrWhiteSpace(input.ResolverVersion) ? DefaultResolverVersion : input.ResolverVersion,
            timeProvider);

        // 2. One package file per resolved id (deduped by construction — `resolved` is keyed by id).
        var packageEntries = resolved
            .Select(kvp => new BundleArchiveEntry($"{kvp.Key}.json", BundleSerializer.SerializePackage(kvp.Value)))
            .ToList();

        // 3. One workflow document per ref, written under the ref's archive filename.
        var workflowEntries = new List<BundleArchiveEntry>(manifest.Workflows.Count);
        foreach (var workflowRef in manifest.Workflows)
        {
            var json = await workflowSource
                .GetWorkflowDocumentAsync(workflowRef, cancellationToken)
                .ConfigureAwait(false);
            workflowEntries.Add(new BundleArchiveEntry(workflowRef.Ref, json));
        }

        return BundleArchiveCodec.Write(new BundleArchive(manifest, @lock, packageEntries, workflowEntries));
    }
}
