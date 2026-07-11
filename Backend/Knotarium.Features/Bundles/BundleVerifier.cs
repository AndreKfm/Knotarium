using System.Collections.Generic;
using System.Linq;
using Knotarium.Infrastructure.Security;

namespace Knotarium.Features.Bundles;

// ─────────────────────────────────────────────────────────────────────────────
// Install-time verification — the security gate the installer runs before touching
// the registry. It is the mirror of BundleResolver: where the resolver *generated*
// the lock (hash + sign + trust) at export, this *re-derives* the same facts from
// the archive's package files and checks them against the lock, then re-evaluates
// trust against the INSTALLER's trusted keys (which may differ from the exporter's).
//
// Pure and DB-free, so the gate is exhaustively testable. Two independent failures
// are distinguished because they mean different things:
//   • hash mismatch  ⇒ the package bytes were tampered with after locking
//   • untrusted      ⇒ the bytes are intact but this host doesn't trust their origin
// Either one blocks install; conflating them would hide tampering behind a trust note.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Why a package did or didn't pass the install gate.</summary>
public enum BundleVerificationStatus
{
    /// <summary>The lock references a package with no matching file under <c>packages/</c>.</summary>
    Missing = 0,

    /// <summary>The package file's re-computed hash does not match the lock — treat as tampered.</summary>
    HashMismatch = 1,

    /// <summary>Intact, but no verified signature and not a local package — this host won't trust it.</summary>
    Untrusted = 2,

    /// <summary>Intact and locally authored; installable only when the caller opts into provisional installs.</summary>
    Provisional = 3,

    /// <summary>Intact and signature-verified against a trusted key.</summary>
    Verified = 4,
}

/// <summary>
/// Factual signature axis, kept separate from the trust <em>policy</em> outcome (ADR-1). Note the limit of
/// the current crypto model: <see cref="PackageSigner.Verify"/> only answers "does this verify against a
/// trusted key", so a cryptographically <em>invalid</em> signature and a <em>valid-but-untrusted-signer</em>
/// signature both land in <see cref="PresentUntrusted"/>. Splitting those two requires the package to carry
/// its signer's public key — a deliberate future change (see docs/bundle-installer-adrs.md, ADR-1).
/// </summary>
public enum PackageSignatureStatus
{
    /// <summary>The package file carries no signature at all.</summary>
    NotPresent = 0,

    /// <summary>A signature is present but did not verify against any of this host's trusted keys.</summary>
    PresentUntrusted = 1,

    /// <summary>The signature verified against a trusted key.</summary>
    VerifiedTrusted = 2,
}

/// <summary>The per-package verdict: what the lock claimed vs. what the archive actually contains.</summary>
public sealed record BundlePackageVerification(
    string PackageId,
    string ExpectedSha256,
    string? ActualSha256,
    bool HashMatches,
    bool SignatureVerified,
    PackageSignatureStatus SignatureStatus,
    PackageTrustLevel TrustLevel,
    BundleVerificationStatus Status,
    bool Installable);

/// <summary>The whole-archive verdict; <see cref="AllInstallable"/> is the single gate the installer checks.</summary>
public sealed record BundleVerificationReport(IReadOnlyList<BundlePackageVerification> Packages)
{
    /// <summary>True only when every locked package verified and is installable under the chosen policy.</summary>
    public bool AllInstallable => Packages.Count > 0
        ? Packages.All(package => package.Installable)
        : true; // A package-free bundle (workflows only) has nothing to gate.

    /// <summary>The packages that blocked install, for surfacing a precise reason to the caller.</summary>
    public IReadOnlyList<BundlePackageVerification> Blocking =>
        Packages.Where(package => !package.Installable).ToList();
}

/// <summary>Pure install-time verification of a bundle archive against its own lock.</summary>
public static class BundleVerifier
{
    /// <summary>
    /// Verifies every package in <paramref name="archive"/>'s lock against the matching <c>packages/</c>
    /// file: re-hashes the carried payload and compares it to the lock, re-checks the signature against
    /// <paramref name="trustedPublicKeysBase64"/>, and re-derives the trust level. A package is installable
    /// only when its hash matches AND its (re-derived) trust passes <see cref="BundleTrust.IsInstallable"/>;
    /// <paramref name="allowProvisional"/> opts into installing locally authored, unsigned packages.
    /// </summary>
    public static BundleVerificationReport Verify(
        BundleArchive archive,
        IReadOnlyList<string> trustedPublicKeysBase64,
        bool allowProvisional = false)
    {
        ArgumentNullException.ThrowIfNull(archive);
        trustedPublicKeysBase64 ??= [];

        // Index package files by id (their archive name is "<id>.json"); a malformed file is treated as absent.
        var filesById = new Dictionary<string, ResolvedBundlePackage>(StringComparer.Ordinal);
        foreach (var entry in archive.Packages)
        {
            try
            {
                var package = BundleSerializer.DeserializePackage(entry.Content);
                filesById[package.Payload.PackageId] = package;
            }
            catch (System.Text.Json.JsonException)
            {
                // Leave it out of the index; the lock entry will report Missing rather than crash the gate.
            }
            catch (System.InvalidOperationException)
            {
            }
        }

        var verifications = archive.Lock.Packages
            .Select(locked => VerifyOne(locked, filesById, trustedPublicKeysBase64, allowProvisional))
            .ToList();

        return new BundleVerificationReport(verifications);
    }

    private static BundlePackageVerification VerifyOne(
        BundleLockPackage locked,
        IReadOnlyDictionary<string, ResolvedBundlePackage> filesById,
        IReadOnlyList<string> trustedKeys,
        bool allowProvisional)
    {
        if (!filesById.TryGetValue(locked.Id, out var package))
        {
            return new BundlePackageVerification(
                locked.Id, locked.Sha256, ActualSha256: null,
                HashMatches: false, SignatureVerified: false,
                SignatureStatus: PackageSignatureStatus.NotPresent,
                TrustLevel: PackageTrustLevel.Untrusted,
                Status: BundleVerificationStatus.Missing, Installable: false);
        }

        var payload = package.Payload;
        var actualSha = BundleHasher.ComputePackageHash(payload.ManifestJson, payload.Source, package.Signature);
        var hashMatches = string.Equals(actualSha, locked.Sha256, StringComparison.OrdinalIgnoreCase);

        var signatureVerified = package.Signature is not null
            && PackageSigner.Verify(payload, package.Signature, trustedKeys);
        var signatureStatus = package.Signature is null
            ? PackageSignatureStatus.NotPresent
            : signatureVerified ? PackageSignatureStatus.VerifiedTrusted : PackageSignatureStatus.PresentUntrusted;
        var trust = BundleTrust.Derive(signatureVerified, payload.Source);

        // Tampered bytes lose first, regardless of trust: a mismatch means the lock no longer describes
        // what we'd install, so re-derived trust is moot.
        var status = !hashMatches
            ? BundleVerificationStatus.HashMismatch
            : trust switch
            {
                PackageTrustLevel.Verified => BundleVerificationStatus.Verified,
                PackageTrustLevel.Provisional => BundleVerificationStatus.Provisional,
                _ => BundleVerificationStatus.Untrusted,
            };

        var installable = hashMatches && BundleTrust.IsInstallable(trust, allowProvisional);

        return new BundlePackageVerification(
            locked.Id, locked.Sha256, actualSha,
            hashMatches, signatureVerified, signatureStatus, trust, status, installable);
    }
}
