using KnotGarden.Core.Domain;
using KnotGarden.Infrastructure.Security;

namespace KnotGarden.Features.Bundles;

// ─────────────────────────────────────────────────────────────────────────────
// Registry → bundle mapping. Turns a stored NodePackage(+version) into the
// ResolvedBundlePackage the resolution pipeline consumes. Pure and DB-free so it
// can be exhaustively unit-tested; the EF query that loads the packages lives in
// RegistryBundlePackageSource.
//
// The field correspondence is load-bearing: the reconstructed PackageSigningPayload
// must match byte-for-byte what was signed at install time (see Program.cs
// InstallNodePackageAsync), or the stored signature won't verify and a genuinely
// trusted package would be downgraded to Untrusted. In particular Source carries
// the *exact* value that was part of the signed payload, not a synthetic token.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Pure mapping from the installed node-package registry to bundle resolution inputs.</summary>
public static class BundlePackageMapping
{
    /// <summary>
    /// Reconstructs the signed <see cref="PackageSigningPayload"/> for <paramref name="version"/> (pulling
    /// display name and category from its parent <paramref name="package"/>) and pairs it with the stored
    /// detached signature, yielding a <see cref="ResolvedBundlePackage"/> candidate. The signature is null
    /// for unsigned packages, which the resolver then treats as unverifiable.
    /// </summary>
    public static ResolvedBundlePackage ToResolvedPackage(NodePackage package, NodePackageVersion version)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(version);

        var payload = new PackageSigningPayload(
            PackageId: package.Id.Value,
            Version: version.Version,
            DisplayName: package.DisplayName,
            Category: package.Category,
            ManifestJson: version.ManifestJson,
            Source: version.Source,
            Capabilities: version.Capabilities);

        return new ResolvedBundlePackage(payload, version.Signature);
    }
}
