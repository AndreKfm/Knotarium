using System.Collections.Generic;
using System.Linq;

namespace KnotGarden.Features.Bundles;

// ─────────────────────────────────────────────────────────────────────────────
// Resolution source — selects which concrete package version satisfies each
// manifest ref, given a set of available packages. This is the step that bridges
// authoring intent (BundlePackageRef.VersionConstraintOrPin) to the concrete
// ResolvedBundlePackage set that BundleResolver hashes and trust-stamps.
//
// Pure: the available set is supplied by the caller. *Where* available packages
// come from (installed registry, on-disk store, a feed) is a later step; this
// only does constraint matching + highest-version selection over what it is given.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Raised when no available package satisfies a manifest ref's id + version constraint.</summary>
public sealed class BundlePackageNotFoundException(string packageId, string constraint)
    : InvalidOperationException(
        $"No available package satisfies '{packageId}' with constraint '{constraint}'.")
{
    public string PackageId { get; } = packageId;
    public string Constraint { get; } = constraint;
}

public static class BundlePackageResolver
{
    /// <summary>
    /// For each ref in <paramref name="refs"/>, selects the highest <paramref name="available"/> package
    /// whose id matches and whose version satisfies the ref's constraint, returning the set keyed by
    /// package id for <see cref="BundleResolver.Resolve"/>. Throws
    /// <see cref="BundlePackageNotFoundException"/> if any ref has no satisfying candidate, and ignores
    /// available packages whose <c>Payload.Version</c> is not a valid semantic version.
    /// </summary>
    public static IReadOnlyDictionary<string, ResolvedBundlePackage> SelectBest(
        IEnumerable<BundlePackageRef> refs,
        IEnumerable<ResolvedBundlePackage> available)
    {
        ArgumentNullException.ThrowIfNull(refs);
        ArgumentNullException.ThrowIfNull(available);

        // Index candidates by id once, parsing versions up front (unparseable versions are skipped).
        var byId = available
            .Select(pkg => (pkg, ok: SemanticVersion.TryParse(pkg.Payload.Version, out var version), version))
            .Where(x => x.ok)
            .GroupBy(x => x.pkg.Payload.PackageId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var selected = new Dictionary<string, ResolvedBundlePackage>();
        foreach (var packageRef in refs)
        {
            if (selected.ContainsKey(packageRef.Id))
            {
                continue; // Duplicate ref for the same id — first constraint wins.
            }

            var constraint = VersionConstraint.Parse(packageRef.VersionConstraintOrPin);
            var best = byId.TryGetValue(packageRef.Id, out var candidates)
                ? candidates
                    .Where(c => constraint.IsSatisfiedBy(c.version!))
                    .OrderByDescending(c => c.version!)
                    .Select(c => c.pkg)
                    .FirstOrDefault()
                : null;

            if (best is null)
            {
                throw new BundlePackageNotFoundException(packageRef.Id, packageRef.VersionConstraintOrPin);
            }

            selected[packageRef.Id] = best;
        }

        return selected;
    }
}
