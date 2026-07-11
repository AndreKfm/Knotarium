using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Knotarium.Features.Bundles;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Knotarium.Tests.Bundles;

public class RegistryBundlePackageSourceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    // Deterministic Ed25519 seed so signed fixtures verify reproducibly.
    private static readonly byte[] PrivateKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly string PublicKeyBase64 = Convert.ToBase64String(PackageSigner.DerivePublicKey(PrivateKey));

    public RegistryBundlePackageSourceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose() => _connection.Dispose();

    private async Task<AppDbContext> CreateContextAsync()
    {
        var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    // A version whose stored payload matches what would have been signed at install time.
    private static NodePackageVersion Version(string packageId, string version, string source, byte[]? signWith)
    {
        var manifestJson = $"{{\"id\":\"{packageId}\",\"version\":\"{version}\"}}";
        var payload = new PackageSigningPayload(
            packageId, version, packageId, "Communication", manifestJson, source, new[] { "http" });

        return new NodePackageVersion
        {
            Id = NodePackageVersionId.New(),
            NodePackageId = NodePackageId.Create(packageId),
            Version = version,
            ManifestJson = manifestJson,
            Source = source,
            Signature = signWith is null ? null : PackageSigner.Sign(payload, signWith),
            Capabilities = new[] { "http" },
            CreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(version.GetHashCode() & 0xff)
        };
    }

    private static async Task SeedAsync(AppDbContext db, string id, string category, params NodePackageVersion[] versions)
    {
        db.NodePackages.Add(new NodePackage
        {
            Id = NodePackageId.Create(id),
            DisplayName = id,
            Category = category,
            Versions = versions.ToList()
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetAvailable_ReturnsEveryVersionOfRequestedPackages()
    {
        await using var db = await CreateContextAsync();
        await SeedAsync(db, "com.example.node", "Communication",
            Version("com.example.node", "1.0.0", "official", PrivateKey),
            Version("com.example.node", "1.2.0", "official", PrivateKey));
        await SeedAsync(db, "com.example.other", "Utility",
            Version("com.example.other", "3.0.0", "local", null));

        var source = new RegistryBundlePackageSource(db);
        var available = await source.GetAvailableAsync(new[] { "com.example.node" });

        // Both versions of the requested package surface; the unrequested package does not.
        Assert.Equal(
            new[] { "1.0.0", "1.2.0" },
            available.Select(p => p.Payload.Version).OrderBy(v => v));
        Assert.All(available, p => Assert.Equal("com.example.node", p.Payload.PackageId));
    }

    [Fact]
    public async Task GetAvailable_UnknownId_ContributesNothing()
    {
        await using var db = await CreateContextAsync();
        await SeedAsync(db, "com.example.node", "Communication",
            Version("com.example.node", "1.0.0", "official", PrivateKey));

        var source = new RegistryBundlePackageSource(db);
        var available = await source.GetAvailableAsync(new[] { "com.example.node", "com.example.ghost" });

        Assert.Single(available);
        Assert.Equal("com.example.node", Assert.Single(available).Payload.PackageId);
    }

    [Fact]
    public async Task GetAvailable_EmptyOrBlankIds_ShortCircuits()
    {
        await using var db = await CreateContextAsync();
        var source = new RegistryBundlePackageSource(db);

        Assert.Empty(await source.GetAvailableAsync(Array.Empty<string>()));
        Assert.Empty(await source.GetAvailableAsync(new[] { "", "   " }));
    }

    [Fact]
    public async Task MappedSignature_VerifiesAgainstTrustedKey()
    {
        await using var db = await CreateContextAsync();
        await SeedAsync(db, "com.example.node", "Communication",
            Version("com.example.node", "1.0.0", "official", PrivateKey));

        var source = new RegistryBundlePackageSource(db);
        var package = Assert.Single(await source.GetAvailableAsync(new[] { "com.example.node" }));

        // The reconstructed payload must match what was signed at install, or this verification fails.
        Assert.True(PackageSigner.Verify(package.Payload, package.Signature!, new[] { PublicKeyBase64 }));
    }

    [Fact]
    public async Task FullPipeline_RegistrySourceThroughResolve_ProducesTrustedLock()
    {
        await using var db = await CreateContextAsync();
        await SeedAsync(db, "com.example.node", "Communication",
            Version("com.example.node", "1.0.0", "official", PrivateKey),
            Version("com.example.node", "1.3.0", "official", PrivateKey));

        var refs = new[] { new BundlePackageRef("com.example.node", ">=1.0.0", "official") };
        var manifest = new BundleManifest(
            "com.example.b", "1.0.0", "B", "Example",
            Array.Empty<string>(), "Communication", 1, "0.9.0",
            refs, Array.Empty<BundleCredentialSlot>(), Array.Empty<BundleWorkflowRef>(),
            new BundleProvenance("local", "Example"));

        var source = new RegistryBundlePackageSource(db);
        var available = await source.GetAvailableAsync(refs.Select(r => r.Id));
        var resolved = BundlePackageResolver.SelectBest(refs, available);
        var @lock = BundleResolver.Resolve(
            manifest, resolved, new[] { PublicKeyBase64 }, "1.0.0", TimeProvider.System);

        var entry = Assert.Single(@lock.Packages);
        // SelectBest picked the highest satisfying version, and the signature verified end-to-end.
        Assert.Equal("1.3.0", entry.ResolvedVersion);
        Assert.Equal("Verified", entry.TrustLevel);
    }
}
