using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Features.Bundles;

// ─────────────────────────────────────────────────────────────────────────────
// Resolution source (IO edge) — the real "available packages" feed for export,
// drawn from the installed node-package registry in the DB. This is what finally
// crosses the dependency-free boundary the pure resolver stack was waiting on:
//
//   manifest refs ─► [this] available set ─► SelectBest ─► Resolve ─► lock
//
// It loads only the packages a manifest actually references (by id), flattens
// every stored version into a candidate, and hands them to BundlePackageResolver
// .SelectBest — which does the constraint matching and highest-version pick. We do
// NOT pre-filter to "latest" here: a bundle ref may pin or cap a version, so the
// selection logic must see every version, not just the newest.
//
// Built-in (in-memory) packages are intentionally out of scope: they ship with the
// engine and carry no signature, so they aren't bundled or trust-derived. A manifest
// that references one surfaces as BundlePackageNotFoundException from SelectBest.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Supplies the resolved-package candidate set for a bundle's manifest refs.</summary>
public interface IBundlePackageSource
{
    /// <summary>
    /// Returns every installed version of each requested package id as a resolution candidate. Unknown
    /// ids simply contribute nothing (the resolver reports the gap); the result is not deduped or
    /// version-filtered — <see cref="BundlePackageResolver.SelectBest"/> owns that.
    /// </summary>
    Task<IReadOnlyList<ResolvedBundlePackage>> GetAvailableAsync(
        IEnumerable<string> packageIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// EF-backed <see cref="IBundlePackageSource"/> over <see cref="AppDbContext.NodePackages"/>. Scoped to
/// the requested ids so export never materializes the whole registry, and read-only (no tracking).
/// </summary>
public sealed class RegistryBundlePackageSource(AppDbContext db) : IBundlePackageSource
{
    public async Task<IReadOnlyList<ResolvedBundlePackage>> GetAvailableAsync(
        IEnumerable<string> packageIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageIds);

        var ids = packageIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => new NodePackageId(id))
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var packages = await db.NodePackages
            .AsNoTracking()
            .Include(package => package.Versions)
            .Where(package => ids.Contains(package.Id))
            .ToListAsync(cancellationToken);

        return packages
            .SelectMany(package => package.Versions
                .Select(version => BundlePackageMapping.ToResolvedPackage(package, version)))
            .ToList();
    }
}
