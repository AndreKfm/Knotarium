// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Knotarium.Features.Bundles;
using Xunit;

namespace Knotarium.Tests.Bundles;

public class BundleModelTests
{
    private static BundleManifest SampleManifest() => new(
        BundleId: "com.example.slack",
        BundleVersion: "1.2.0",
        Name: "Slack",
        Publisher: "Example",
        Tags: new[] { "slack", "chat" },
        Category: "Communication",
        SchemaVersion: 1,
        MinEngineVersion: "0.9.0",
        Packages: new[]
        {
            new BundlePackageRef("com.example.slack.node", ">=1.0.0", "official")
        },
        CredentialSlots: new[]
        {
            new BundleCredentialSlot(
                Slot: "slackToken",
                Type: "apiKey",
                DisplayName: "Slack token",
                Description: "Bot token with chat:write",
                Checklist: new[] { "Create a Slack app", "Add chat:write scope", "Copy the bot token" })
        },
        Workflows: new[]
        {
            new BundleWorkflowRef("postMessage", "sample", "postMessage.json")
        },
        Provenance: new BundleProvenance("local", "Example"));

    private static BundleLock SampleLock() => new(
        Packages: new[]
        {
            new BundleLockPackage(
                Id: "com.example.slack.node",
                ResolvedVersion: "1.3.1",
                Sha256: "abc123",
                ResolvedSource: "local",
                TrustLevel: "Provisional")
        },
        ResolvedAt: "2026-06-17T00:00:00Z",
        ResolverVersion: "1.0.0");

    [Fact]
    public void Manifest_RoundTrips_Losslessly()
    {
        var manifest = SampleManifest();

        var json = BundleSerializer.SerializeManifest(manifest);
        var parsed = BundleSerializer.DeserializeManifest(json);

        // Re-serializing the parsed value reproduces the original JSON byte-for-byte => no field lost
        // or mangled. (Record == uses reference equality for collection members, so compare via JSON.)
        Assert.Equal(json, BundleSerializer.SerializeManifest(parsed));
        Assert.Equal(manifest.BundleId, parsed.BundleId);
        Assert.Equal(manifest.Packages.Count, parsed.Packages.Count);
        Assert.Equal(manifest.CredentialSlots[0].Checklist.Count, parsed.CredentialSlots[0].Checklist.Count);
    }

    [Fact]
    public void Lock_RoundTrips_Losslessly()
    {
        var @lock = SampleLock();

        var json = BundleSerializer.SerializeLock(@lock);
        var parsed = BundleSerializer.DeserializeLock(json);

        Assert.Equal(json, BundleSerializer.SerializeLock(parsed));
        Assert.Equal(@lock.Packages[0].Sha256, parsed.Packages[0].Sha256);
        Assert.Equal(@lock.ResolverVersion, parsed.ResolverVersion);
    }

    [Fact]
    public void ComputePackageHash_IsDeterministic()
    {
        const string manifestJson = "{\"id\":\"node\",\"version\":\"1.0.0\"}";

        var a = BundleHasher.ComputePackageHash(manifestJson, "official", "sig");
        var b = BundleHasher.ComputePackageHash(manifestJson, "official", "sig");

        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputePackageHash_IsSensitiveToInputs()
    {
        const string manifestJson = "{\"id\":\"node\",\"version\":\"1.0.0\"}";

        var baseline = BundleHasher.ComputePackageHash(manifestJson, "official", "sig");

        Assert.NotEqual(baseline, BundleHasher.ComputePackageHash(manifestJson, "local", "sig"));
        Assert.NotEqual(baseline, BundleHasher.ComputePackageHash(manifestJson, "official", "other"));
        Assert.NotEqual(
            baseline,
            BundleHasher.ComputePackageHash("{\"id\":\"node\",\"version\":\"2.0.0\"}", "official", "sig"));
    }

    [Fact]
    public void ComputePackageHash_IsCanonical_OrderIndependentWithinManifest()
    {
        var ordered = BundleHasher.ComputePackageHash("{\"a\":1,\"b\":2}", "official", null);
        var reordered = BundleHasher.ComputePackageHash("{\"b\":2,\"a\":1}", "official", null);

        Assert.Equal(ordered, reordered);
    }

    [Fact]
    public void ComputeBytesHash_MatchesKnownSha256()
    {
        var bytes = Encoding.UTF8.GetBytes("knotarium");
        var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.Equal(expected, BundleHasher.ComputeBytesHash(bytes));
    }

    [Fact]
    public void Manifest_DoesNotLeak_LockOnlyFields()
    {
        var json = BundleSerializer.SerializeManifest(SampleManifest());

        Assert.DoesNotContain("sha256", json, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trustLevel", json, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resolvedSource", json, System.StringComparison.OrdinalIgnoreCase);
    }
}
