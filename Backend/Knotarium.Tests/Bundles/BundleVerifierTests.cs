// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Knotarium.Features.Bundles;
using Knotarium.Infrastructure.Security;
using Xunit;

namespace Knotarium.Tests.Bundles;

public class BundleVerifierTests
{
    private static readonly byte[] PrivateKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly string PublicKeyBase64 = Convert.ToBase64String(PackageSigner.DerivePublicKey(PrivateKey));

    private static PackageSigningPayload Payload(string id, string source, string version = "1.0.0") => new(
        id, version, id, "Communication", $"{{\"id\":\"{id}\"}}", source, new[] { "http" });

    // Builds an archive whose lock entry is consistent with the carried package file (the export path's output).
    private static BundleArchive ArchiveWith(ResolvedBundlePackage package, string? overrideLockSha = null)
    {
        var payload = package.Payload;
        var sha = overrideLockSha
            ?? BundleHasher.ComputePackageHash(payload.ManifestJson, payload.Source, package.Signature);

        var manifest = new BundleManifest(
            "com.example.b", "1.0.0", "B", "Example",
            Array.Empty<string>(), "Communication", 1, "0.9.0",
            new[] { new BundlePackageRef(payload.PackageId, ">=1.0.0", payload.Source) },
            Array.Empty<BundleCredentialSlot>(), Array.Empty<BundleWorkflowRef>(),
            new BundleProvenance(payload.Source, "Example"));

        var @lock = new BundleLock(
            new[] { new BundleLockPackage(payload.PackageId, payload.Version, sha, payload.Source, "Verified") },
            "1970-01-01T00:00:00.0000000", "1.0.0");

        return new BundleArchive(
            manifest, @lock,
            new[] { new BundleArchiveEntry($"{payload.PackageId}.json", BundleSerializer.SerializePackage(package)) },
            Array.Empty<BundleArchiveEntry>());
    }

    private static ResolvedBundlePackage Signed(string id, string source) =>
        new(Payload(id, source), PackageSigner.Sign(Payload(id, source), PrivateKey));

    [Fact]
    public void Verify_SignedIntactPackage_IsVerifiedAndInstallable()
    {
        var report = BundleVerifier.Verify(ArchiveWith(Signed("com.example.node", "official")), new[] { PublicKeyBase64 });

        var entry = Assert.Single(report.Packages);
        Assert.True(entry.HashMatches);
        Assert.True(entry.SignatureVerified);
        Assert.Equal(PackageSignatureStatus.VerifiedTrusted, entry.SignatureStatus);
        Assert.Equal(BundleVerificationStatus.Verified, entry.Status);
        Assert.True(entry.Installable);
        Assert.True(report.AllInstallable);
    }

    [Fact]
    public void Verify_HashMismatch_IsTamperedAndBlocked()
    {
        // Lock claims a hash that doesn't match the carried payload => tampered.
        var archive = ArchiveWith(Signed("com.example.node", "official"), overrideLockSha: "0000deadbeef");

        var entry = Assert.Single(BundleVerifier.Verify(archive, new[] { PublicKeyBase64 }).Packages);
        Assert.False(entry.HashMatches);
        Assert.Equal(BundleVerificationStatus.HashMismatch, entry.Status);
        Assert.False(entry.Installable);
    }

    [Fact]
    public void Verify_SignedButKeyNotTrusted_IsUntrustedAndBlocked()
    {
        // Intact bytes (hash matches), but the installer trusts no keys => origin untrusted.
        var report = BundleVerifier.Verify(ArchiveWith(Signed("com.example.node", "official")), Array.Empty<string>());

        var entry = Assert.Single(report.Packages);
        Assert.True(entry.HashMatches);
        Assert.False(entry.SignatureVerified);
        // Signature is present (the package is signed) but didn't verify against this host's keys.
        Assert.Equal(PackageSignatureStatus.PresentUntrusted, entry.SignatureStatus);
        Assert.Equal(BundleVerificationStatus.Untrusted, entry.Status);
        Assert.False(entry.Installable);
        Assert.False(report.AllInstallable);
    }

    [Fact]
    public void Verify_UnsignedLocalPackage_IsProvisional_InstallableOnlyWhenOptedIn()
    {
        var archive = ArchiveWith(new ResolvedBundlePackage(Payload("com.example.node", "local"), Signature: null));

        var blocked = Assert.Single(BundleVerifier.Verify(archive, Array.Empty<string>()).Packages);
        Assert.Equal(BundleVerificationStatus.Provisional, blocked.Status);
        Assert.Equal(PackageSignatureStatus.NotPresent, blocked.SignatureStatus);
        Assert.False(blocked.Installable);

        var allowed = Assert.Single(BundleVerifier.Verify(archive, Array.Empty<string>(), allowProvisional: true).Packages);
        Assert.True(allowed.Installable);
    }

    [Fact]
    public void Verify_MissingPackageFile_IsMissingAndBlocked()
    {
        var archive = ArchiveWith(Signed("com.example.node", "official"));
        // Drop the package file but keep the lock entry referencing it.
        var stripped = archive with { Packages = Array.Empty<BundleArchiveEntry>() };

        var entry = Assert.Single(BundleVerifier.Verify(stripped, new[] { PublicKeyBase64 }).Packages);
        Assert.Equal(BundleVerificationStatus.Missing, entry.Status);
        Assert.Null(entry.ActualSha256);
        Assert.False(entry.Installable);
    }

    [Fact]
    public void Verify_NoPackages_AllInstallableIsTrue()
    {
        var manifest = new BundleManifest(
            "com.example.b", "1.0.0", "B", "Example",
            Array.Empty<string>(), "Communication", 1, "0.9.0",
            Array.Empty<BundlePackageRef>(), Array.Empty<BundleCredentialSlot>(),
            Array.Empty<BundleWorkflowRef>(), new BundleProvenance("local", "Example"));
        var archive = new BundleArchive(
            manifest, new BundleLock(Array.Empty<BundleLockPackage>(), "x", "1.0.0"),
            Array.Empty<BundleArchiveEntry>(), Array.Empty<BundleArchiveEntry>());

        Assert.True(BundleVerifier.Verify(archive, Array.Empty<string>()).AllInstallable);
    }
}
