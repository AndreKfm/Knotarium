using System;
using System.Collections.Generic;
using System.Globalization;
using KnotGarden.Features.Bundles;
using KnotGarden.Infrastructure.Security;
using Xunit;

namespace KnotGarden.Tests.Bundles;

public class BundleResolverTests
{
    // Deterministic 32-byte Ed25519 seed so signature verification is reproducible across runs.
    private static readonly byte[] PrivateKey = BuildSeed();
    private static readonly string PublicKeyBase64 = Convert.ToBase64String(PackageSigner.DerivePublicKey(PrivateKey));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static byte[] BuildSeed()
    {
        var seed = new byte[32];
        for (var i = 0; i < seed.Length; i++)
        {
            seed[i] = (byte)(i + 1);
        }

        return seed;
    }

    private static PackageSigningPayload Payload(string id, string source, string version = "1.0.0") => new(
        PackageId: id,
        Version: version,
        DisplayName: id,
        Category: "Communication",
        ManifestJson: $"{{\"id\":\"{id}\",\"version\":\"{version}\"}}",
        Source: source,
        Capabilities: new[] { "http" });

    private static BundleManifest ManifestWith(params string[] packageIds)
    {
        var refs = new List<BundlePackageRef>();
        foreach (var id in packageIds)
        {
            refs.Add(new BundlePackageRef(id, ">=1.0.0", "official"));
        }

        return new BundleManifest(
            BundleId: "com.example.b",
            BundleVersion: "1.0.0",
            Name: "B",
            Publisher: "Example",
            Tags: Array.Empty<string>(),
            Category: "Communication",
            SchemaVersion: 1,
            MinEngineVersion: "0.9.0",
            Packages: refs,
            CredentialSlots: Array.Empty<BundleCredentialSlot>(),
            Workflows: Array.Empty<BundleWorkflowRef>(),
            Provenance: new BundleProvenance("local", "Example"));
    }

    private static TimeProvider AtEpoch() => new FixedTimeProvider(DateTimeOffset.UnixEpoch);

    [Fact]
    public void Resolve_SignedPackageWithTrustedKey_IsVerified()
    {
        var payload = Payload("com.example.node", source: "official");
        var signature = PackageSigner.Sign(payload, PrivateKey);
        var resolved = new Dictionary<string, ResolvedBundlePackage>
        {
            ["com.example.node"] = new(payload, signature)
        };

        var @lock = BundleResolver.Resolve(
            ManifestWith("com.example.node"), resolved, new[] { PublicKeyBase64 }, "1.0.0", AtEpoch());

        var entry = Assert.Single(@lock.Packages);
        Assert.Equal("Verified", entry.TrustLevel);
        Assert.Equal("1.0.0", entry.ResolvedVersion);
        Assert.Equal("official", entry.ResolvedSource);
        Assert.Equal(
            BundleHasher.ComputePackageHash(payload.ManifestJson, payload.Source, signature),
            entry.Sha256);
    }

    [Fact]
    public void Resolve_SignedButKeyNotTrusted_IsUntrusted()
    {
        var payload = Payload("com.example.node", source: "official");
        var signature = PackageSigner.Sign(payload, PrivateKey);
        var resolved = new Dictionary<string, ResolvedBundlePackage>
        {
            ["com.example.node"] = new(payload, signature)
        };

        // Empty trusted-key set => signature cannot verify => official+unverified is Untrusted.
        var @lock = BundleResolver.Resolve(
            ManifestWith("com.example.node"), resolved, Array.Empty<string>(), "1.0.0", AtEpoch());

        Assert.Equal("Untrusted", Assert.Single(@lock.Packages).TrustLevel);
    }

    [Fact]
    public void Resolve_UnsignedLocalPackage_IsProvisional()
    {
        var payload = Payload("com.example.node", source: "local");
        var resolved = new Dictionary<string, ResolvedBundlePackage>
        {
            ["com.example.node"] = new(payload, Signature: null)
        };

        var @lock = BundleResolver.Resolve(
            ManifestWith("com.example.node"), resolved, new[] { PublicKeyBase64 }, "1.0.0", AtEpoch());

        Assert.Equal("Provisional", Assert.Single(@lock.Packages).TrustLevel);
    }

    [Fact]
    public void Resolve_TamperedSignature_DoesNotVerify()
    {
        var payload = Payload("com.example.node", source: "official");
        var signature = PackageSigner.Sign(payload, PrivateKey);
        // Resolve against a *different* payload than was signed (version bumped) => verification fails.
        var tampered = payload with { Version = "9.9.9" };
        var resolved = new Dictionary<string, ResolvedBundlePackage>
        {
            ["com.example.node"] = new(tampered, signature)
        };

        var @lock = BundleResolver.Resolve(
            ManifestWith("com.example.node"), resolved, new[] { PublicKeyBase64 }, "1.0.0", AtEpoch());

        Assert.Equal("Untrusted", Assert.Single(@lock.Packages).TrustLevel);
    }

    [Fact]
    public void Resolve_MissingPackage_Throws()
    {
        var ex = Assert.Throws<BundleResolutionException>(() => BundleResolver.Resolve(
            ManifestWith("com.example.missing"),
            new Dictionary<string, ResolvedBundlePackage>(),
            Array.Empty<string>(),
            "1.0.0",
            AtEpoch()));

        Assert.Equal("com.example.missing", ex.PackageId);
    }

    [Fact]
    public void Resolve_PreservesPackageOrderAndStampsClock()
    {
        var a = Payload("com.example.a", source: "local");
        var b = Payload("com.example.b", source: "local");
        var resolved = new Dictionary<string, ResolvedBundlePackage>
        {
            ["com.example.b"] = new(b, null),
            ["com.example.a"] = new(a, null)
        };

        var when = new DateTimeOffset(2026, 6, 17, 8, 30, 0, TimeSpan.Zero);
        var @lock = BundleResolver.Resolve(
            ManifestWith("com.example.a", "com.example.b"),
            resolved,
            Array.Empty<string>(),
            "2.0.0",
            new FixedTimeProvider(when));

        // Lock order follows the manifest's declared order, not dictionary iteration order.
        Assert.Equal(new[] { "com.example.a", "com.example.b" }, new[] { @lock.Packages[0].Id, @lock.Packages[1].Id });
        Assert.Equal("2.0.0", @lock.ResolverVersion);
        Assert.Equal(when.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), @lock.ResolvedAt);
    }
}
