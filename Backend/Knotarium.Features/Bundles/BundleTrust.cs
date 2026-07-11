namespace Knotarium.Features.Bundles;

// ─────────────────────────────────────────────────────────────────────────────
// Trust model — derives a per-package trust level from its signature verdict and
// resolved source, and maps that level to/from the string token the lock carries
// (see BundleLockPackage.TrustLevel). Kept deliberately free of the bundle records
// so the format stays decoupled from the enum, and free of DI/IO so it is a pure,
// exhaustively testable function the install path can call directly.
//
// The architecture's verification invariant (D3): a package claiming a remote/
// official origin without a signature verified against trusted keys must not be
// trusted. That maps to: verified signature ⇒ Verified; otherwise only a locally
// authored package is allowed through as Provisional; everything else is Untrusted.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// How much the host trusts a resolved package. Ordered least-to-most trusted so
/// callers can gate with simple comparisons (e.g. <c>level &gt;= Provisional</c>).
/// </summary>
public enum PackageTrustLevel
{
    /// <summary>No verifiable provenance — a remote/official claim without a valid signature. Never installable.</summary>
    Untrusted = 0,

    /// <summary>Locally authored and unsigned. Allowed in authoring/local contexts, flagged for the user.</summary>
    Provisional = 1,

    /// <summary>Signature verified against a trusted key (host-signed or a trusted third party).</summary>
    Verified = 2,
}

/// <summary>
/// Pure trust derivation and token mapping for bundle packages. Stateless: the caller
/// supplies the signature verdict (computed via <c>PackageSigner.Verify</c>) and the
/// resolved source; this decides the trust level and translates it for the lock.
/// </summary>
public static class BundleTrust
{
    /// <summary>The source token denoting a locally authored package (the only Provisional-eligible source).</summary>
    public const string LocalSource = "local";

    /// <summary>
    /// Derives the trust level from whether the package's signature verified against trusted keys
    /// and its resolved source. A verified signature is sufficient for <see cref="PackageTrustLevel.Verified"/>
    /// regardless of source; without one, only a <see cref="LocalSource"/> package earns
    /// <see cref="PackageTrustLevel.Provisional"/>, and everything else is <see cref="PackageTrustLevel.Untrusted"/>.
    /// </summary>
    public static PackageTrustLevel Derive(bool signatureVerified, string? resolvedSource)
    {
        if (signatureVerified)
        {
            return PackageTrustLevel.Verified;
        }

        return IsLocalSource(resolvedSource)
            ? PackageTrustLevel.Provisional
            : PackageTrustLevel.Untrusted;
    }

    /// <summary>True when the level is safe to install. Provisional requires an explicit local/author opt-in.</summary>
    public static bool IsInstallable(PackageTrustLevel level, bool allowProvisional = false) => level switch
    {
        PackageTrustLevel.Verified => true,
        PackageTrustLevel.Provisional => allowProvisional,
        _ => false,
    };

    /// <summary>The token written into <see cref="BundleLockPackage.TrustLevel"/> (the enum's name).</summary>
    public static string ToToken(PackageTrustLevel level) => level.ToString();

    /// <summary>
    /// Parses the lock's trust token. Unrecognised or missing tokens fall back to
    /// <see cref="PackageTrustLevel.Untrusted"/> — fail closed, never silently trust an unknown value.
    /// </summary>
    public static PackageTrustLevel ParseToken(string? token) =>
        Enum.TryParse<PackageTrustLevel>(token, ignoreCase: true, out var level) && Enum.IsDefined(level)
            ? level
            : PackageTrustLevel.Untrusted;

    private static bool IsLocalSource(string? source) =>
        string.Equals(source?.Trim(), LocalSource, StringComparison.OrdinalIgnoreCase);
}
