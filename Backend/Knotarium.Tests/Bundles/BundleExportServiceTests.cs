using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Features.Bundles;
using Knotarium.Infrastructure.Security;
using Xunit;

namespace Knotarium.Tests.Bundles;

public class BundleExportServiceTests
{
    private static readonly byte[] PrivateKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly string PublicKeyBase64 = Convert.ToBase64String(PackageSigner.DerivePublicKey(PrivateKey));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // In-memory package source: returns a fixed candidate set regardless of requested ids.
    private sealed class FakePackageSource(IReadOnlyList<ResolvedBundlePackage> available) : IBundlePackageSource
    {
        public IReadOnlyList<string>? LastRequestedIds { get; private set; }

        public Task<IReadOnlyList<ResolvedBundlePackage>> GetAvailableAsync(
            IEnumerable<string> packageIds, CancellationToken cancellationToken = default)
        {
            LastRequestedIds = packageIds.ToList();
            return Task.FromResult(available);
        }
    }

    // In-memory workflow source: serves canned JSON keyed by the ref's Key, or throws when absent.
    private sealed class FakeWorkflowSource(IReadOnlyDictionary<string, string> docs) : IBundleWorkflowSource
    {
        public Task<string> GetWorkflowDocumentAsync(
            BundleWorkflowRef workflowRef, CancellationToken cancellationToken = default)
        {
            if (!docs.TryGetValue(workflowRef.Key, out var json))
            {
                throw new BundleWorkflowNotFoundException(workflowRef.Key);
            }

            return Task.FromResult(json);
        }
    }

    private static ResolvedBundlePackage SignedPackage(string id, string version, string source)
    {
        var payload = new PackageSigningPayload(
            id, version, id, "Communication", $"{{\"id\":\"{id}\"}}", source, new[] { "http" });
        return new ResolvedBundlePackage(payload, PackageSigner.Sign(payload, PrivateKey));
    }

    private static BundleManifest Manifest(
        IReadOnlyList<BundlePackageRef> packages, IReadOnlyList<BundleWorkflowRef> workflows) => new(
        BundleId: "com.example.bundle",
        BundleVersion: "2.0.0",
        Name: "Example",
        Publisher: "Example",
        Tags: Array.Empty<string>(),
        Category: "Communication",
        SchemaVersion: 1,
        MinEngineVersion: "0.9.0",
        Packages: packages,
        CredentialSlots: Array.Empty<BundleCredentialSlot>(),
        Workflows: workflows,
        Provenance: new BundleProvenance("local", "Example"));

    private static BundleExportService Service(
        FakePackageSource packages, FakeWorkflowSource workflows) =>
        new(packages, workflows, new FixedTimeProvider(DateTimeOffset.UnixEpoch));

    [Fact]
    public async Task ExportAsync_ProducesArchiveThatRoundTripsWithLockAndFiles()
    {
        var packageSource = new FakePackageSource(new[]
        {
            SignedPackage("com.example.node", "1.0.0", "official"),
            SignedPackage("com.example.node", "1.4.0", "official"),
        });
        var workflowSource = new FakeWorkflowSource(new Dictionary<string, string>
        {
            ["wf-main"] = "{\"manifest\":{},\"content\":{}}"
        });
        var manifest = Manifest(
            new[] { new BundlePackageRef("com.example.node", ">=1.0.0", "official") },
            new[] { new BundleWorkflowRef("wf-main", "primary", "main.json") });

        var bytes = await Service(packageSource, workflowSource)
            .ExportAsync(new BundleExportInput(manifest, new[] { PublicKeyBase64 }, "9.9.9"));

        var archive = BundleArchiveCodec.Read(bytes);

        // Lock: highest satisfying version, verified against the trusted key, resolver version stamped.
        var lockEntry = Assert.Single(archive.Lock.Packages);
        Assert.Equal("1.4.0", lockEntry.ResolvedVersion);
        Assert.Equal("Verified", lockEntry.TrustLevel);
        Assert.Equal("9.9.9", archive.Lock.ResolverVersion);

        // Package file: one per resolved id, re-parseable and signature still valid.
        var packageEntry = Assert.Single(archive.Packages);
        Assert.Equal("com.example.node.json", packageEntry.Name);
        var package = BundleSerializer.DeserializePackage(packageEntry.Content);
        Assert.Equal("1.4.0", package.Payload.Version);
        Assert.True(PackageSigner.Verify(package.Payload, package.Signature!, new[] { PublicKeyBase64 }));

        // Workflow file: written under the ref's archive filename with the source's content.
        var workflowEntry = Assert.Single(archive.Workflows);
        Assert.Equal("main.json", workflowEntry.Name);
        Assert.Equal("{\"manifest\":{},\"content\":{}}", workflowEntry.Content);

        // The package source was queried with exactly the manifest's referenced ids.
        Assert.Equal(new[] { "com.example.node" }, packageSource.LastRequestedIds);
    }

    [Fact]
    public async Task ExportAsync_UnsatisfiablePackage_ThrowsAndEmitsNothing()
    {
        var packageSource = new FakePackageSource(Array.Empty<ResolvedBundlePackage>());
        var manifest = Manifest(
            new[] { new BundlePackageRef("com.example.ghost", ">=1.0.0", "official") },
            Array.Empty<BundleWorkflowRef>());

        await Assert.ThrowsAsync<BundlePackageNotFoundException>(() =>
            Service(packageSource, new FakeWorkflowSource(new Dictionary<string, string>()))
                .ExportAsync(new BundleExportInput(manifest, Array.Empty<string>(), "1.0.0")));
    }

    [Fact]
    public async Task ExportAsync_MissingWorkflow_ThrowsBundleWorkflowNotFound()
    {
        var packageSource = new FakePackageSource(Array.Empty<ResolvedBundlePackage>());
        var manifest = Manifest(
            Array.Empty<BundlePackageRef>(),
            new[] { new BundleWorkflowRef("wf-absent", "primary", "absent.json") });

        var ex = await Assert.ThrowsAsync<BundleWorkflowNotFoundException>(() =>
            Service(packageSource, new FakeWorkflowSource(new Dictionary<string, string>()))
                .ExportAsync(new BundleExportInput(manifest, Array.Empty<string>(), "1.0.0")));

        Assert.Equal("wf-absent", ex.WorkflowKey);
    }

    [Fact]
    public async Task ExportAsync_NoPackagesOrWorkflows_StillProducesValidArchive()
    {
        var bytes = await Service(
                new FakePackageSource(Array.Empty<ResolvedBundlePackage>()),
                new FakeWorkflowSource(new Dictionary<string, string>()))
            .ExportAsync(new BundleExportInput(
                Manifest(Array.Empty<BundlePackageRef>(), Array.Empty<BundleWorkflowRef>()),
                Array.Empty<string>(),
                ResolverVersion: ""));

        var archive = BundleArchiveCodec.Read(bytes);
        Assert.Empty(archive.Packages);
        Assert.Empty(archive.Workflows);
        Assert.Empty(archive.Lock.Packages);
        // Blank resolver version falls back to the service default.
        Assert.Equal(BundleExportService.DefaultResolverVersion, archive.Lock.ResolverVersion);
    }
}
