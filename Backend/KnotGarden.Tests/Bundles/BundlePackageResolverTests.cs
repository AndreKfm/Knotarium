using System;
using System.Collections.Generic;
using System.Linq;
using KnotGarden.Features.Bundles;
using KnotGarden.Infrastructure.Security;
using Xunit;

namespace KnotGarden.Tests.Bundles;

public class BundlePackageResolverTests
{
    private static ResolvedBundlePackage Pkg(string id, string version, string source = "official") => new(
        new PackageSigningPayload(
            PackageId: id,
            Version: version,
            DisplayName: id,
            Category: "Communication",
            ManifestJson: $"{{\"id\":\"{id}\",\"version\":\"{version}\"}}",
            Source: source,
            Capabilities: Array.Empty<string>()),
        Signature: null);

    private static BundlePackageRef Ref(string id, string constraint) => new(id, constraint, "official");

    [Fact]
    public void SelectBest_PicksHighestSatisfyingVersion()
    {
        var available = new[]
        {
            Pkg("node", "1.0.0"),
            Pkg("node", "1.4.2"),
            Pkg("node", "2.0.0"),
        };

        var selected = BundlePackageResolver.SelectBest(new[] { Ref("node", "^1.0.0") }, available);

        Assert.Equal("1.4.2", selected["node"].Payload.Version);
    }

    [Fact]
    public void SelectBest_ExactPin_SelectsThatVersion()
    {
        var available = new[] { Pkg("node", "1.0.0"), Pkg("node", "1.1.0") };

        var selected = BundlePackageResolver.SelectBest(new[] { Ref("node", "1.0.0") }, available);

        Assert.Equal("1.0.0", selected["node"].Payload.Version);
    }

    [Fact]
    public void SelectBest_ResolvesEachRefIndependently()
    {
        var available = new[]
        {
            Pkg("a", "1.0.0"),
            Pkg("a", "1.2.0"),
            Pkg("b", "3.1.0"),
        };

        var selected = BundlePackageResolver.SelectBest(
            new[] { Ref("a", ">=1.1.0"), Ref("b", "*") }, available);

        Assert.Equal("1.2.0", selected["a"].Payload.Version);
        Assert.Equal("3.1.0", selected["b"].Payload.Version);
    }

    [Fact]
    public void SelectBest_NoSatisfyingVersion_Throws()
    {
        var available = new[] { Pkg("node", "1.0.0") };

        var ex = Assert.Throws<BundlePackageNotFoundException>(() =>
            BundlePackageResolver.SelectBest(new[] { Ref("node", ">=2.0.0") }, available));

        Assert.Equal("node", ex.PackageId);
        Assert.Equal(">=2.0.0", ex.Constraint);
    }

    [Fact]
    public void SelectBest_UnknownId_Throws()
    {
        var ex = Assert.Throws<BundlePackageNotFoundException>(() =>
            BundlePackageResolver.SelectBest(new[] { Ref("ghost", "*") }, Array.Empty<ResolvedBundlePackage>()));

        Assert.Equal("ghost", ex.PackageId);
    }

    [Fact]
    public void SelectBest_IgnoresUnparseableVersions()
    {
        var available = new[]
        {
            Pkg("node", "not-a-version"),
            Pkg("node", "1.0.0"),
        };

        var selected = BundlePackageResolver.SelectBest(new[] { Ref("node", "*") }, available);

        Assert.Equal("1.0.0", selected["node"].Payload.Version);
    }

    [Fact]
    public void SelectBest_FeedsResolverToProduceLock()
    {
        // The selected set is the exact shape BundleResolver.Resolve consumes.
        var available = new[] { Pkg("node", "1.0.0", source: "local"), Pkg("node", "1.5.0", source: "local") };
        var manifest = new BundleManifest(
            "com.example.b", "1.0.0", "B", "Example",
            Array.Empty<string>(), "Communication", 1, "0.9.0",
            new[] { Ref("node", "^1.0.0") },
            Array.Empty<BundleCredentialSlot>(),
            Array.Empty<BundleWorkflowRef>(),
            new BundleProvenance("local", "Example"));

        var selected = BundlePackageResolver.SelectBest(manifest.Packages, available);
        var @lock = BundleResolver.Resolve(manifest, selected, Array.Empty<string>(), "1.0.0", TimeProvider.System);

        var entry = Assert.Single(@lock.Packages);
        Assert.Equal("1.5.0", entry.ResolvedVersion);
        Assert.Equal("Provisional", entry.TrustLevel); // unsigned + local source
    }
}
