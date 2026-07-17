// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Api.Services;
using Knotarium.Features.Execution;
using Knotarium.Features.Portability;
using Knotarium.Features.Bundles;
using Knotarium.Features.Compiler;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Knotarium.Tests.Bundles;

public class BundleInstallServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    private static readonly byte[] PrivateKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly string PublicKeyBase64 = Convert.ToBase64String(PackageSigner.DerivePublicKey(PrivateKey));

    public BundleInstallServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose() => _connection.Dispose();

    // Node property values may be plain strings (in-memory) or JsonElement (deserialized) — read either.
    private static string? PropString(object value) =>
        value is JsonElement element ? element.GetString() : value as string;

    private async Task<AppDbContext> CreateContextAsync()
    {
        var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    // Records each imported document; returns an incrementing version number.
    private sealed class FakeWorkflowImporter : IBundleWorkflowImporter
    {
        public List<string> ImportedWorkflowIds { get; } = new();
        public List<WorkflowExportDocument> ImportedDocuments { get; } = new();

        public Task<int> ImportAsync(WorkflowExportDocument document, CancellationToken cancellationToken = default)
        {
            ImportedWorkflowIds.Add(document.Manifest.WorkflowId);
            ImportedDocuments.Add(document);
            return Task.FromResult(ImportedWorkflowIds.Count);
        }
    }

    private static PackageSigningPayload Payload(string id, string source, string version = "1.0.0") => new(
        id, version, id, "Communication", $"{{\"id\":\"{id}\"}}", source, new[] { "http" });

    private static string WorkflowDocJson(string workflowId, IReadOnlyList<NodeDefinition>? nodes = null)
    {
        var version = new WorkflowVersion(
            WorkflowVersionId.New(),
            new WorkflowDefinitionId(workflowId),
            1,
            nodes ?? Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>(),
            DateTimeOffset.UnixEpoch);
        return WorkflowVersionSerializer.Serialize(version, workflowId);
    }

    // Assembles a .kgbundle whose lock is consistent with its package file.
    private static byte[] BuildBundle(
        ResolvedBundlePackage package,
        (string Key, string Ref, string WorkflowId)? workflow = null)
    {
        var payload = package.Payload;
        var sha = BundleHasher.ComputePackageHash(payload.ManifestJson, payload.Source, package.Signature);

        var workflowRefs = workflow is { } w
            ? new[] { new BundleWorkflowRef(w.Key, "primary", w.Ref) }
            : Array.Empty<BundleWorkflowRef>();
        var workflowEntries = workflow is { } wf
            ? new[] { new BundleArchiveEntry(wf.Ref, WorkflowDocJson(wf.WorkflowId)) }
            : Array.Empty<BundleArchiveEntry>();

        var manifest = new BundleManifest(
            "com.example.b", "1.0.0", "B", "Example",
            Array.Empty<string>(), "Communication", 1, "0.9.0",
            new[] { new BundlePackageRef(payload.PackageId, ">=1.0.0", payload.Source) },
            new[] { new BundleCredentialSlot("smtp", "smtp", "SMTP", null, Array.Empty<string>()) },
            workflowRefs, new BundleProvenance(payload.Source, "Example"));

        var @lock = new BundleLock(
            new[] { new BundleLockPackage(payload.PackageId, payload.Version, sha, payload.Source, "Verified") },
            "1970-01-01T00:00:00.0000000", "1.0.0");

        return BundleArchiveCodec.Write(new BundleArchive(
            manifest, @lock,
            new[] { new BundleArchiveEntry($"{payload.PackageId}.json", BundleSerializer.SerializePackage(package)) },
            workflowEntries));
    }

    private static ResolvedBundlePackage Signed(string id, string source, string version = "1.0.0") =>
        new(Payload(id, source, version), PackageSigner.Sign(Payload(id, source, version), PrivateKey));

    // A signed package with a custom manifest, so two same-version packages can differ in bytes (and hash).
    private static ResolvedBundlePackage SignedWith(string id, string source, string version, string manifestJson)
    {
        var payload = new PackageSigningPayload(id, version, id, "Communication", manifestJson, source, new[] { "http" });
        return new ResolvedBundlePackage(payload, PackageSigner.Sign(payload, PrivateKey));
    }

    [Fact]
    public async Task InstallAsync_VerifiedBundle_InstallsPackageAndImportsWorkflow()
    {
        await using var db = await CreateContextAsync();
        var importer = new FakeWorkflowImporter();
        var service = new BundleInstallService(db, importer, new InMemoryNodePackageManifestProvider());

        var bytes = BuildBundle(
            Signed("com.example.node", "official"),
            workflow: ("wf-main", "main.json", "wf-1"));

        var result = await service.InstallAsync(bytes, new[] { PublicKeyBase64 });

        Assert.True(result.Installed);
        Assert.Equal(new[] { "com.example.node@1.0.0" }, result.InstalledPackages);
        Assert.Equal(new[] { "wf-1" }, importer.ImportedWorkflowIds);
        Assert.Equal("smtp", Assert.Single(result.RequiredCredentialSlots).Slot);

        // The package is actually in the registry now.
        var stored = await db.NodePackages.Include(p => p.Versions)
            .FirstOrDefaultAsync(p => p.Id == NodePackageId.Create("com.example.node"));
        Assert.NotNull(stored);
        Assert.Equal("1.0.0", Assert.Single(stored!.Versions).Version);
    }

    [Fact]
    public async Task InstallAsync_UntrustedPackage_BlocksAndWritesNothing()
    {
        await using var db = await CreateContextAsync();
        var importer = new FakeWorkflowImporter();
        var service = new BundleInstallService(db, importer, new InMemoryNodePackageManifestProvider());

        // Signed, but the installer trusts no keys => official+unverified is Untrusted => blocked.
        var bytes = BuildBundle(Signed("com.example.node", "official"), workflow: ("wf-main", "main.json", "wf-1"));

        var result = await service.InstallAsync(bytes, Array.Empty<string>());

        Assert.False(result.Installed);
        Assert.Empty(result.InstalledPackages);
        Assert.Empty(importer.ImportedWorkflowIds);           // gate fails before any workflow import
        Assert.False(await db.NodePackages.AnyAsync());        // nothing written
        Assert.NotEmpty(result.Verification.Blocking);
    }

    [Fact]
    public async Task InstallAsync_TamperedHash_BlocksAndWritesNothing()
    {
        await using var db = await CreateContextAsync();
        var service = new BundleInstallService(db, new FakeWorkflowImporter(), new InMemoryNodePackageManifestProvider());

        // Re-pack the package file with a different source than the lock was hashed over => hash mismatch.
        var package = Signed("com.example.node", "official");
        var bytes = BuildBundle(package);
        var archive = BundleArchiveCodec.Read(bytes);
        var tamperedFile = new BundleArchiveEntry(
            "com.example.node.json",
            BundleSerializer.SerializePackage(new ResolvedBundlePackage(
                package.Payload with { Source = "evil" }, package.Signature)));
        var tampered = BundleArchiveCodec.Write(archive with { Packages = new[] { tamperedFile } });

        var result = await service.InstallAsync(tampered, new[] { PublicKeyBase64 });

        Assert.False(result.Installed);
        Assert.Equal(BundleVerificationStatus.HashMismatch, Assert.Single(result.Verification.Packages).Status);
        Assert.False(await db.NodePackages.AnyAsync());
    }

    [Fact]
    public async Task InstallAsync_ReinstallSameVersion_IsIdempotent()
    {
        await using var db = await CreateContextAsync();
        var service = new BundleInstallService(db, new FakeWorkflowImporter(), new InMemoryNodePackageManifestProvider());
        var bytes = BuildBundle(Signed("com.example.node", "official"));

        var first = await service.InstallAsync(bytes, new[] { PublicKeyBase64 });
        var second = await service.InstallAsync(bytes, new[] { PublicKeyBase64 });

        Assert.Equal(new[] { "com.example.node@1.0.0" }, first.InstalledPackages);
        Assert.True(second.Installed);
        Assert.Empty(second.InstalledPackages);                       // nothing new added
        Assert.Equal(new[] { "com.example.node@1.0.0" }, second.SkippedPackages);

        var stored = await db.NodePackages.Include(p => p.Versions)
            .FirstAsync(p => p.Id == NodePackageId.Create("com.example.node"));
        Assert.Single(stored.Versions);                               // not duplicated
    }

    [Fact]
    public async Task InstallAsync_SameVersionDifferentHash_ConflictsAndPreservesInstalled()
    {
        await using var db = await CreateContextAsync();
        var service = new BundleInstallService(db, new FakeWorkflowImporter(), new InMemoryNodePackageManifestProvider());

        // First install: version 1.0.0 with manifest A.
        var first = SignedWith("com.example.node", "official", "1.0.0", "{\"id\":\"com.example.node\"}");
        var installed = await service.InstallAsync(BuildBundle(first), new[] { PublicKeyBase64 });
        Assert.True(installed.Installed);

        // Second install: SAME id@1.0.0 but different bytes (manifest B) — a hard conflict, not a skip.
        var second = SignedWith("com.example.node", "official", "1.0.0", "{\"id\":\"com.example.node\",\"x\":1}");
        var result = await service.InstallAsync(BuildBundle(second), new[] { PublicKeyBase64 });

        Assert.False(result.Installed);
        Assert.Equal(new[] { "com.example.node@1.0.0" }, result.ConflictingPackages);

        // The originally installed bytes are untouched — the conflicting bundle did not overwrite them.
        var stored = await db.NodePackages.Include(p => p.Versions)
            .FirstAsync(p => p.Id == NodePackageId.Create("com.example.node"));
        Assert.Equal("{\"id\":\"com.example.node\"}", Assert.Single(stored.Versions).ManifestJson);
    }

    [Fact]
    public async Task InstallAsync_RebindsCredentialSlotsInImportedWorkflow()
    {
        await using var db = await CreateContextAsync();
        var importer = new FakeWorkflowImporter();
        var service = new BundleInstallService(db, importer, new InMemoryNodePackageManifestProvider());

        // A workflow node referencing a credential via a slot placeholder.
        var slotNode = new NodeDefinition(
            NodeId.Create("http"), "http",
            new Dictionary<string, object> { ["apiKeySecretRef"] = "slot:smtp" });

        var package = Signed("com.example.node", "official");
        var sha = BundleHasher.ComputePackageHash(package.Payload.ManifestJson, package.Payload.Source, package.Signature);
        var manifest = new BundleManifest(
            "com.example.b", "1.0.0", "B", "Example",
            Array.Empty<string>(), "Communication", 1, "0.9.0",
            new[] { new BundlePackageRef("com.example.node", ">=1.0.0", "official") },
            new[] { new BundleCredentialSlot("smtp", "smtp", "SMTP", null, Array.Empty<string>()) },
            new[] { new BundleWorkflowRef("wf-main", "primary", "main.json") },
            new BundleProvenance("official", "Example"));
        var @lock = new BundleLock(
            new[] { new BundleLockPackage("com.example.node", "1.0.0", sha, "official", "Verified") },
            "1970-01-01T00:00:00.0000000", "1.0.0");
        var bytes = BundleArchiveCodec.Write(new BundleArchive(
            manifest, @lock,
            new[] { new BundleArchiveEntry("com.example.node.json", BundleSerializer.SerializePackage(package)) },
            new[] { new BundleArchiveEntry("main.json", WorkflowDocJson("wf-1", new[] { slotNode })) }));

        var result = await service.InstallAsync(
            bytes, new[] { PublicKeyBase64 },
            credentialBindings: new Dictionary<string, string> { ["smtp"] = "cred-xyz" });

        Assert.True(result.Installed);
        Assert.Equal(new[] { "smtp" }, result.ReboundCredentialSlots);
        Assert.Empty(result.UnboundCredentialSlots);

        // The importer received the rewritten document — placeholder resolved to the real id.
        var importedNode = Assert.Single(importer.ImportedDocuments[0].Content.Nodes);
        Assert.Equal("cred-xyz", PropString(importedNode.Properties["apiKeySecretRef"]));
    }

    [Fact]
    public async Task InstallAsync_UnboundSlot_StillInstallsAndReportsIt()
    {
        await using var db = await CreateContextAsync();
        var importer = new FakeWorkflowImporter();
        var service = new BundleInstallService(db, importer, new InMemoryNodePackageManifestProvider());

        var slotNode = new NodeDefinition(
            NodeId.Create("http"), "http",
            new Dictionary<string, object> { ["apiKeySecretRef"] = "slot:smtp" });
        var package = Signed("com.example.node", "official");
        var sha = BundleHasher.ComputePackageHash(package.Payload.ManifestJson, package.Payload.Source, package.Signature);
        var manifest = new BundleManifest(
            "com.example.b", "1.0.0", "B", "Example",
            Array.Empty<string>(), "Communication", 1, "0.9.0",
            new[] { new BundlePackageRef("com.example.node", ">=1.0.0", "official") },
            Array.Empty<BundleCredentialSlot>(),
            new[] { new BundleWorkflowRef("wf-main", "primary", "main.json") },
            new BundleProvenance("official", "Example"));
        var @lock = new BundleLock(
            new[] { new BundleLockPackage("com.example.node", "1.0.0", sha, "official", "Verified") },
            "1970-01-01T00:00:00.0000000", "1.0.0");
        var bytes = BundleArchiveCodec.Write(new BundleArchive(
            manifest, @lock,
            new[] { new BundleArchiveEntry("com.example.node.json", BundleSerializer.SerializePackage(package)) },
            new[] { new BundleArchiveEntry("main.json", WorkflowDocJson("wf-1", new[] { slotNode })) }));

        // No bindings supplied: install still succeeds, placeholder preserved, slot reported as unbound.
        var result = await service.InstallAsync(bytes, new[] { PublicKeyBase64 });

        Assert.True(result.Installed);
        Assert.Equal(new[] { "smtp" }, result.UnboundCredentialSlots);
        Assert.Equal("slot:smtp", PropString(Assert.Single(importer.ImportedDocuments[0].Content.Nodes).Properties["apiKeySecretRef"]));
    }

    [Fact]
    public async Task InstallAsync_PrivilegedNodes_BlockedUntilAcknowledged()
    {
        await using var db = await CreateContextAsync();
        var importer = new FakeWorkflowImporter();
        var service = new BundleInstallService(db, importer, new InMemoryNodePackageManifestProvider());

        // A workflow carrying a built-in privileged node (fileWrite → filesystem.write).
        var fileNode = new NodeDefinition(
            NodeId.Create("fw"), "fileWrite",
            new Dictionary<string, object> { ["path"] = "/tmp/x" });
        var package = Signed("com.example.node", "official");
        var sha = BundleHasher.ComputePackageHash(package.Payload.ManifestJson, package.Payload.Source, package.Signature);
        var manifest = new BundleManifest(
            "com.example.b", "1.0.0", "B", "Example",
            Array.Empty<string>(), "Communication", 1, "0.9.0",
            new[] { new BundlePackageRef("com.example.node", ">=1.0.0", "official") },
            Array.Empty<BundleCredentialSlot>(),
            new[] { new BundleWorkflowRef("wf-main", "primary", "main.json") },
            new BundleProvenance("official", "Example"));
        var @lock = new BundleLock(
            new[] { new BundleLockPackage("com.example.node", "1.0.0", sha, "official", "Verified") },
            "1970-01-01T00:00:00.0000000", "1.0.0");
        var bytes = BundleArchiveCodec.Write(new BundleArchive(
            manifest, @lock,
            new[] { new BundleArchiveEntry("com.example.node.json", BundleSerializer.SerializePackage(package)) },
            new[] { new BundleArchiveEntry("main.json", WorkflowDocJson("wf-1", new[] { fileNode })) }));

        // First attempt: blocked, nothing written, privileged nodes surfaced.
        var blocked = await service.InstallAsync(bytes, new[] { PublicKeyBase64 });
        Assert.False(blocked.Installed);
        Assert.True(blocked.PrivilegedAcknowledgementRequired);
        Assert.Contains(blocked.PrivilegedNodes, p => p.NodeType == "fileWrite");
        Assert.Empty(importer.ImportedDocuments);

        // Acknowledged: installs.
        var ok = await service.InstallAsync(bytes, new[] { PublicKeyBase64 }, acknowledgePrivileged: true);
        Assert.True(ok.Installed);
    }

    [Fact]
    public async Task InstallAsync_ProvisionalLocalPackage_RequiresOptIn()
    {
        await using var db = await CreateContextAsync();
        var service = new BundleInstallService(db, new FakeWorkflowImporter(), new InMemoryNodePackageManifestProvider());
        var bytes = BuildBundle(new ResolvedBundlePackage(Payload("com.example.node", "local"), Signature: null));

        var blocked = await service.InstallAsync(bytes, Array.Empty<string>());
        Assert.False(blocked.Installed);

        var allowed = await service.InstallAsync(bytes, Array.Empty<string>(), allowProvisional: true);
        Assert.True(allowed.Installed);
        Assert.Equal(new[] { "com.example.node@1.0.0" }, allowed.InstalledPackages);
    }
}
