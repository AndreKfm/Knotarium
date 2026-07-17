// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

using Knotarium.Features.Execution;
using Knotarium.Features.Portability;
using Knotarium.Features.Security;

namespace Knotarium.Features.Bundles;

// ─────────────────────────────────────────────────────────────────────────────
// Install orchestration — the mirror of BundleExportService. Reads a .kgbundle,
// runs the pure BundleVerifier gate, and ONLY if every package passes does it
// touch the registry:
//
//   bytes ─► Read ─► Verify ─┬─ (blocked) ─► report, nothing installed
//                            └─ (ok) ─► install packages ─► import workflows
//
// Verify-then-apply is the whole safety story: a tampered or untrusted package
// stops the install before any DB write, so an install never half-trusts a bundle.
// DB writes run in one transaction; credential-slot rebinding is deferred — the
// manifest's required slots are surfaced so the caller can bind them afterwards.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>An imported workflow: its manifest key plus the inactive version created for it.</summary>
public sealed record BundleWorkflowInstall(string Key, string WorkflowId, int VersionNumber);

/// <summary>The outcome of an install attempt — the verification report plus what was applied (if anything).</summary>
public sealed record BundleInstallResult(
    bool Installed,
    BundleVerificationReport Verification,
    IReadOnlyList<string> InstalledPackages,
    IReadOnlyList<string> SkippedPackages,
    IReadOnlyList<BundleWorkflowInstall> ImportedWorkflows,
    IReadOnlyList<BundleCredentialSlot> RequiredCredentialSlots,
    IReadOnlyList<string> ReboundCredentialSlots,
    IReadOnlyList<string> UnboundCredentialSlots,
    IReadOnlyList<string> ConflictingPackages,
    IReadOnlyList<PrivilegedNodeInfo> PrivilegedNodes,
    // True when the install was held back solely because privileged nodes weren't acknowledged — the caller
    // can re-submit with acknowledgePrivileged once the user confirms the warning.
    bool PrivilegedAcknowledgementRequired);

/// <summary>Verifies and installs a <c>.kgbundle</c> into the registry, importing its workflows.</summary>
public sealed class BundleInstallService(
    AppDbContext dbContext,
    IBundleWorkflowImporter workflowImporter,
    INodePackageManifestProvider manifestProvider)
{
    /// <summary>
    /// Reads, verifies, and (when the gate passes) installs <paramref name="bundleBytes"/>. When any package
    /// fails verification nothing is written and <see cref="BundleInstallResult.Installed"/> is false; the
    /// report names the blocking packages. Package versions already present are skipped (idempotent re-install),
    /// and each bundled workflow is imported as a new inactive version via <see cref="WorkflowPublisher"/>.
    /// </summary>
    public async Task<BundleInstallResult> InstallAsync(
        byte[] bundleBytes,
        IReadOnlyList<string> trustedPublicKeysBase64,
        bool allowProvisional = false,
        IReadOnlyDictionary<string, string>? credentialBindings = null,
        bool acknowledgePrivileged = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundleBytes);
        credentialBindings ??= new Dictionary<string, string>();

        var archive = BundleArchiveCodec.Read(bundleBytes);
        var report = BundleVerifier.Verify(archive, trustedPublicKeysBase64 ?? [], allowProvisional);

        if (!report.AllInstallable)
        {
            // Hard stop before any mutation — the gate is the point.
            return Blocked(report, archive, conflicts: []);
        }

        // Privileged-node gate: an imported bundle is untrusted input. Surface any filesystem/code/database
        // nodes and refuse to write until the caller explicitly acknowledges them.
        var privilegedNodes = await ScanPrivilegedAsync(archive, cancellationToken).ConfigureAwait(false);
        if (privilegedNodes.Count > 0 && !acknowledgePrivileged)
        {
            return Blocked(report, archive, conflicts: [], privilegedNodes, privilegedAckRequired: true);
        }

        // Re-parse the verified package files by id; the gate guarantees each locked package has a valid one.
        var packagesById = archive.Packages
            .Select(entry => BundleSerializer.DeserializePackage(entry.Content))
            .ToDictionary(package => package.Payload.PackageId, StringComparer.Ordinal);

        // Pre-flight: a package whose exact version is already installed but with DIFFERENT bytes is a hard
        // conflict (ADR-2) — silently skipping it would bind the workflow to whatever is installed instead of
        // the bundle's pinned package. The lock's Sha256 is the bundle bytes' authoritative hash (the gate
        // just confirmed the file matches it), so we compare the installed version against that. Reject the
        // whole install before any write rather than half-apply.
        var conflicts = await DetectVersionConflictsAsync(archive.Lock.Packages, packagesById, cancellationToken)
            .ConfigureAwait(false);
        if (conflicts.Count > 0)
        {
            return Blocked(report, archive, conflicts);
        }

        var installed = new List<string>();
        var skipped = new List<string>();
        var importedWorkflows = new List<BundleWorkflowInstall>();
        var reboundSlots = new HashSet<string>(StringComparer.Ordinal);
        var unboundSlots = new HashSet<string>(StringComparer.Ordinal);

        // NOTE (residue seam): this transaction covers DB writes (packages, versions, workflow versions),
        // so a failure here rolls those back cleanly. It does NOT cover the workflow *draft* that
        // WorkflowPublisher.ImportAsync writes via the file-based IWorkflowStore — a mid-import failure can
        // leave a draft file behind while the DB rolls back. Tracked as a known partial-residue limitation
        // (see docs/bundle-installer-adrs.md, "Failure & residue").
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var locked in archive.Lock.Packages)
        {
            var package = packagesById[locked.Id];
            if (await InstallPackageAsync(package, cancellationToken).ConfigureAwait(false))
            {
                installed.Add($"{locked.Id}@{package.Payload.Version}");
            }
            else
            {
                skipped.Add($"{locked.Id}@{package.Payload.Version}");
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var workflowRef in archive.Manifest.Workflows)
        {
            var entry = archive.Workflows.FirstOrDefault(w => string.Equals(w.Name, workflowRef.Ref, StringComparison.Ordinal));
            if (entry is null)
            {
                throw new BundleWorkflowNotFoundException(workflowRef.Key);
            }

            var document = WorkflowVersionSerializer.Deserialize(entry.Content);

            // Rewrite slot:<Slot> credential placeholders to real ids before the version is created.
            var rebind = BundleCredentialRebinder.Rebind(document, credentialBindings);
            foreach (var slot in rebind.ReboundSlots) reboundSlots.Add(slot);
            foreach (var slot in rebind.UnboundSlots) unboundSlots.Add(slot);

            var versionNumber = await workflowImporter.ImportAsync(rebind.Document, cancellationToken).ConfigureAwait(false);
            importedWorkflows.Add(new BundleWorkflowInstall(
                workflowRef.Key, rebind.Document.Manifest.WorkflowId, versionNumber));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new BundleInstallResult(
            Installed: true, report,
            installed, skipped, importedWorkflows, archive.Manifest.CredentialSlots,
            reboundSlots.ToList(), unboundSlots.ToList(), ConflictingPackages: [],
            privilegedNodes, PrivilegedAcknowledgementRequired: false);
    }

    /// <summary>Collect every node across the bundle's workflows and flag the privileged ones.</summary>
    private async Task<IReadOnlyList<PrivilegedNodeInfo>> ScanPrivilegedAsync(BundleArchive archive, CancellationToken cancellationToken)
    {
        var nodes = new List<NodeDefinition>();
        foreach (var entry in archive.Workflows)
        {
            var document = WorkflowVersionSerializer.Deserialize(entry.Content);
            nodes.AddRange(document.Content.Nodes);
        }
        return await PrivilegedNodeScanner.ScanAsync(manifestProvider, nodes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds a "nothing applied" result, carrying the reason (gate report, conflicts, and/or the
    /// privileged nodes awaiting acknowledgement).</summary>
    private static BundleInstallResult Blocked(
        BundleVerificationReport report, BundleArchive archive, IReadOnlyList<string> conflicts,
        IReadOnlyList<PrivilegedNodeInfo>? privilegedNodes = null, bool privilegedAckRequired = false) =>
        new(Installed: false, report,
            InstalledPackages: [], SkippedPackages: [],
            ImportedWorkflows: [], RequiredCredentialSlots: archive.Manifest.CredentialSlots,
            ReboundCredentialSlots: [], UnboundCredentialSlots: [], ConflictingPackages: conflicts,
            privilegedNodes ?? [], privilegedAckRequired);

    /// <summary>
    /// Finds packages whose exact version is already installed but with different bytes than the bundle's.
    /// Read-only: it never mutates, so the caller can reject before opening the write transaction.
    /// </summary>
    private async Task<IReadOnlyList<string>> DetectVersionConflictsAsync(
        IReadOnlyList<BundleLockPackage> lockedPackages,
        IReadOnlyDictionary<string, ResolvedBundlePackage> packagesById,
        CancellationToken cancellationToken)
    {
        var conflicts = new List<string>();
        foreach (var locked in lockedPackages)
        {
            var packageId = NodePackageId.Create(locked.Id);
            var version = packagesById[locked.Id].Payload.Version;

            var installed = await dbContext.NodePackages
                .AsNoTracking()
                .Where(p => p.Id == packageId)
                .SelectMany(p => p.Versions)
                .FirstOrDefaultAsync(v => v.Version == version, cancellationToken)
                .ConfigureAwait(false);

            // Same version, different bytes (vs the lock's authoritative hash) ⇒ conflict.
            if (installed is not null
                && !string.Equals(InstalledHash(installed), locked.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add($"{locked.Id}@{version}");
            }
        }

        return conflicts;
    }

    /// <summary>Hashes an installed version the same way the lock was computed, for byte-equality comparison.</summary>
    private static string InstalledHash(NodePackageVersion version) =>
        BundleHasher.ComputePackageHash(version.ManifestJson, version.Source, version.Signature);

    /// <summary>
    /// Upserts the package and adds its version, reconstructing the row from the verified signed payload
    /// (mirroring the manual install path). Returns false — a no-op skip — when that exact version already
    /// exists; the pre-flight conflict check guarantees an existing same-version row has identical bytes, so
    /// the skip is true idempotent reuse and never silently masks a different package.
    /// </summary>
    private async Task<bool> InstallPackageAsync(ResolvedBundlePackage package, CancellationToken cancellationToken)
    {
        var payload = package.Payload;
        var packageId = NodePackageId.Create(payload.PackageId);

        var existing = await dbContext.NodePackages
            .Include(p => p.Versions)
            .FirstOrDefaultAsync(p => p.Id == packageId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            existing = new NodePackage
            {
                Id = packageId,
                DisplayName = payload.DisplayName,
                Category = payload.Category,
            };
            dbContext.NodePackages.Add(existing);
        }
        else if (existing.Versions.Any(v => string.Equals(v.Version, payload.Version, StringComparison.Ordinal)))
        {
            return false; // Idempotent reuse (pre-flight already proved identical bytes).
        }

        existing.Versions.Add(new NodePackageVersion
        {
            Id = NodePackageVersionId.New(),
            NodePackageId = packageId,
            Version = payload.Version,
            ManifestJson = payload.ManifestJson,
            Source = payload.Source,
            Signature = package.Signature,
            Capabilities = payload.Capabilities,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        return true;
    }
}
