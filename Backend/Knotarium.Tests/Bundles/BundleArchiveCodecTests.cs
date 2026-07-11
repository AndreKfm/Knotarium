using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Knotarium.Features.Bundles;
using Xunit;

namespace Knotarium.Tests.Bundles;

public class BundleArchiveCodecTests
{
    private static BundleManifest SampleManifest() => new(
        BundleId: "com.example.bundle",
        BundleVersion: "1.2.0",
        Name: "Example Bundle",
        Publisher: "Example",
        Tags: new[] { "demo", "test" },
        Category: "Communication",
        SchemaVersion: 1,
        MinEngineVersion: "0.9.0",
        Packages: new[] { new BundlePackageRef("com.example.node", ">=1.0.0", "official") },
        CredentialSlots: new[]
        {
            new BundleCredentialSlot("smtp", "smtp", "SMTP Server", "Outbound mail", new[] { "host", "port" })
        },
        Workflows: new[] { new BundleWorkflowRef("main", "primary", "main.json") },
        Provenance: new BundleProvenance("local", "Example"));

    private static BundleLock SampleLock() => new(
        Packages: new[]
        {
            new BundleLockPackage("com.example.node", "1.0.0", "deadbeef", "official", "Verified")
        },
        ResolvedAt: "1970-01-01T00:00:00.0000000",
        ResolverVersion: "1.0.0");

    private static BundleArchive SampleArchive() => new(
        SampleManifest(),
        SampleLock(),
        Packages: new[] { new BundleArchiveEntry("com.example.node.json", "{\"id\":\"com.example.node\"}") },
        Workflows: new[] { new BundleArchiveEntry("main.json", "{\"manifest\":{},\"content\":{}}") });

    [Fact]
    public void WriteThenRead_RoundTripsEveryField()
    {
        var original = SampleArchive();

        var restored = BundleArchiveCodec.Read(BundleArchiveCodec.Write(original));

        // Manifest/lock equality is checked through their serialized form: record .Equals compares the
        // list *references*, so an array and a round-tripped List never compare equal despite identical
        // contents. The serialized text is the property that actually matters for round-tripping.
        Assert.Equal(
            BundleSerializer.SerializeManifest(original.Manifest),
            BundleSerializer.SerializeManifest(restored.Manifest));
        Assert.Equal(
            BundleSerializer.SerializeLock(original.Lock),
            BundleSerializer.SerializeLock(restored.Lock));
        Assert.Equal(original.Packages, restored.Packages);
        Assert.Equal(original.Workflows, restored.Workflows);
    }

    [Fact]
    public void Write_IsDeterministic_SameContentSameBytes()
    {
        var first = BundleArchiveCodec.Write(SampleArchive());
        var second = BundleArchiveCodec.Write(SampleArchive());

        Assert.Equal(first, second);
    }

    [Fact]
    public void Write_PackageOrderDoesNotChangeBytes()
    {
        var a = new BundleArchiveEntry("a.json", "A");
        var b = new BundleArchiveEntry("b.json", "B");
        var forward = SampleArchive() with { Packages = new[] { a, b } };
        var reversed = SampleArchive() with { Packages = new[] { b, a } };

        // Entries are written in canonical path order, so input ordering can't perturb the bytes.
        Assert.Equal(BundleArchiveCodec.Write(forward), BundleArchiveCodec.Write(reversed));
    }

    [Fact]
    public void Write_ProducesExpectedEntryNames()
    {
        var bytes = BundleArchiveCodec.Write(SampleArchive());

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();

        Assert.Contains("bundle.json", names);
        Assert.Contains("bundle.lock", names);
        Assert.Contains("packages/com.example.node.json", names);
        Assert.Contains("workflows/main.json", names);
    }

    [Fact]
    public void Read_MissingManifest_Throws()
    {
        var bytes = ZipWith(("bundle.lock", "{}"));

        var ex = Assert.Throws<BundleArchiveException>(() => BundleArchiveCodec.Read(bytes));
        Assert.Contains("bundle.json", ex.Message);
    }

    [Fact]
    public void Read_MissingLock_Throws()
    {
        var bytes = ZipWith(("bundle.json", BundleSerializer.SerializeManifest(SampleManifest())));

        var ex = Assert.Throws<BundleArchiveException>(() => BundleArchiveCodec.Read(bytes));
        Assert.Contains("bundle.lock", ex.Message);
    }

    [Fact]
    public void Read_UnexpectedEntry_ThrowsRatherThanDropping()
    {
        var bytes = ZipWith(
            ("bundle.json", BundleSerializer.SerializeManifest(SampleManifest())),
            ("bundle.lock", BundleSerializer.SerializeLock(SampleLock())),
            ("stray.txt", "nope"));

        var ex = Assert.Throws<BundleArchiveException>(() => BundleArchiveCodec.Read(bytes));
        Assert.Contains("stray.txt", ex.Message);
    }

    [Fact]
    public void Read_NotAZip_Throws()
    {
        Assert.Throws<BundleArchiveException>(
            () => BundleArchiveCodec.Read(Encoding.UTF8.GetBytes("this is not a zip archive at all")));
    }

    [Fact]
    public void Read_IgnoresDirectoryPlaceholderEntries()
    {
        // A zip produced elsewhere may include explicit folder entries; they must not be misread as files.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("packages/");
            Write(zip, "bundle.json", BundleSerializer.SerializeManifest(SampleManifest()));
            Write(zip, "bundle.lock", BundleSerializer.SerializeLock(SampleLock()));
        }

        var restored = BundleArchiveCodec.Read(buffer.ToArray());
        Assert.Empty(restored.Packages);
    }

    [Fact]
    public void Write_DuplicatePackageName_Throws()
    {
        var archive = SampleArchive() with
        {
            Packages = new[]
            {
                new BundleArchiveEntry("dup.json", "1"),
                new BundleArchiveEntry("dup.json", "2"),
            }
        };

        Assert.Throws<BundleArchiveException>(() => BundleArchiveCodec.Write(archive));
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("nested/child.json")]
    [InlineData("")]
    public void Write_InvalidLeafName_Throws(string name)
    {
        var archive = SampleArchive() with { Workflows = new[] { new BundleArchiveEntry(name, "x") } };

        Assert.Throws<BundleArchiveException>(() => BundleArchiveCodec.Write(archive));
    }

    private static byte[] ZipWith(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                Write(zip, name, content);
            }
        }

        return buffer.ToArray();
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
