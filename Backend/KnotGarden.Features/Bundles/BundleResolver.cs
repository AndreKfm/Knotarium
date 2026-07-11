using System.Globalization;
using KnotGarden.Infrastructure.Security;

namespace KnotGarden.Features.Bundles;

// ─────────────────────────────────────────────────────────────────────────────
// Lock generation — resolves a manifest's package refs into a bundle.lock by
// hashing each resolved package (BundleHasher), verifying its signature against
// the host's trusted keys (PackageSigner), and deriving its trust level
// (BundleTrust). This is the one place that turns authoring intent into the
// verified, trusted record the installer consumes.
//
// Kept pure: the caller supplies the already-resolved packages, so this has no
// IO/registry/disk dependency. *How* refs are resolved to concrete versions
// (registry lookup, on-disk scan, version-constraint solving) is a later step;
// this only computes the lock from a resolved set.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A manifest package ref resolved to a concrete, signable package. <see cref="Payload"/> carries the
/// exact bytes the host signed (id, version, source, manifest, capabilities); <see cref="Signature"/> is
/// the detached Ed25519 signature, or null when the package is unsigned.
/// </summary>
public sealed record ResolvedBundlePackage(PackageSigningPayload Payload, string? Signature);

/// <summary>Raised when a manifest references a package that the resolved set does not contain.</summary>
public sealed class BundleResolutionException(string packageId)
    : InvalidOperationException($"The bundle manifest references package '{packageId}', but it was not resolved.")
{
    public string PackageId { get; } = packageId;
}

/// <summary>Pure manifest → lock resolution. Stateless; the only ambient input is the supplied clock.</summary>
public static class BundleResolver
{
    /// <summary>
    /// Builds the <see cref="BundleLock"/> for <paramref name="manifest"/> from the resolved packages.
    /// Every <see cref="BundleManifest.Packages"/> entry must have a matching key in
    /// <paramref name="resolved"/> or this throws <see cref="BundleResolutionException"/> — the lock is
    /// never silently partial. Trust is derived per package from signature verification against
    /// <paramref name="trustedPublicKeysBase64"/>; with no trusted keys, only locally sourced packages
    /// resolve above <see cref="PackageTrustLevel.Untrusted"/>.
    /// </summary>
    public static BundleLock Resolve(
        BundleManifest manifest,
        IReadOnlyDictionary<string, ResolvedBundlePackage> resolved,
        IReadOnlyList<string> trustedPublicKeysBase64,
        string resolverVersion,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(timeProvider);
        trustedPublicKeysBase64 ??= [];

        var lockPackages = new List<BundleLockPackage>(manifest.Packages.Count);
        foreach (var packageRef in manifest.Packages)
        {
            if (!resolved.TryGetValue(packageRef.Id, out var package))
            {
                throw new BundleResolutionException(packageRef.Id);
            }

            lockPackages.Add(ResolveOne(package, trustedPublicKeysBase64));
        }

        var resolvedAt = timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        return new BundleLock(lockPackages, resolvedAt, resolverVersion);
    }

    private static BundleLockPackage ResolveOne(ResolvedBundlePackage package, IReadOnlyList<string> trustedKeys)
    {
        var payload = package.Payload;
        var sha256 = BundleHasher.ComputePackageHash(payload.ManifestJson, payload.Source, package.Signature);

        var signatureVerified = package.Signature is not null
            && PackageSigner.Verify(payload, package.Signature, trustedKeys);
        var trust = BundleTrust.Derive(signatureVerified, payload.Source);

        return new BundleLockPackage(
            Id: payload.PackageId,
            ResolvedVersion: payload.Version,
            Sha256: sha256,
            ResolvedSource: payload.Source,
            TrustLevel: BundleTrust.ToToken(trust));
    }
}
