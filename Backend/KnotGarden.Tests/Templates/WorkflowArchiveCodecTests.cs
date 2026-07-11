using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using KnotGarden.Features.Portability;
using Xunit;

namespace KnotGarden.Tests.Templates;

public class WorkflowArchiveCodecTests
{
    private static readonly WorkflowArchiveLimits Tiny = new(
        MaxArchiveBytes: 4096,
        MaxTotalUncompressedBytes: 8192,
        MaxEntryBytes: 4096,
        MaxCompressionRatio: 100,
        MaxEntryCount: 8);

    [Fact]
    public void Write_then_read_round_trips_and_sorts_paths()
    {
        var entries = new Dictionary<string, string> { ["b.json"] = "B", ["a.json"] = "A", ["nested/c.json"] = "C" };
        var bytes = WorkflowArchiveCodec.Write(entries);

        var read = WorkflowArchiveCodec.Read(bytes, WorkflowArchiveLimits.Default);

        Assert.Equal("A", read["a.json"]);
        Assert.Equal("B", read["b.json"]);
        Assert.Equal("C", read["nested/c.json"]);
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("/rooted.json")]
    [InlineData("a/../b.json")]
    [InlineData("back\\slash.json")]
    public void Write_rejects_unsafe_paths(string path)
    {
        var entries = new Dictionary<string, string> { [path] = "x" };
        Assert.Throws<WorkflowArchiveException>(() => WorkflowArchiveCodec.Write(entries));
    }

    [Fact]
    public void Read_rejects_archive_over_byte_limit()
    {
        var entries = new Dictionary<string, string> { ["a.json"] = new string('x', 10_000) };
        var bytes = WorkflowArchiveCodec.Write(entries);
        Assert.Throws<WorkflowArchiveException>(() => WorkflowArchiveCodec.Read(bytes, Tiny));
    }

    [Fact]
    public void Read_rejects_entry_over_total_uncompressed_limit()
    {
        // Highly compressible payload: small archive, large inflation — the total-uncompressed guard fires.
        var entries = new Dictionary<string, string> { ["a.json"] = new string('a', 50_000) };
        var bytes = WorkflowArchiveCodec.Write(entries);
        Assert.Throws<WorkflowArchiveException>(() => WorkflowArchiveCodec.Read(bytes, Tiny));
    }

    [Fact]
    public void Read_rejects_too_many_entries()
    {
        var entries = Enumerable.Range(0, 20).ToDictionary(i => $"f{i}.json", _ => "x");
        var bytes = WorkflowArchiveCodec.Write(entries);
        Assert.Throws<WorkflowArchiveException>(() => WorkflowArchiveCodec.Read(bytes, Tiny));
    }

    [Fact]
    public void Read_rejects_invalid_utf8()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("a.json");
            using var stream = entry.Open();
            stream.Write(new byte[] { 0xff, 0xfe, 0xfd }, 0, 3);
        }

        Assert.Throws<WorkflowArchiveException>(() => WorkflowArchiveCodec.Read(memory.ToArray(), WorkflowArchiveLimits.Default));
    }

    [Fact]
    public void Read_rejects_corrupt_bytes()
    {
        Assert.Throws<WorkflowArchiveException>(
            () => WorkflowArchiveCodec.Read(new byte[] { 9, 8, 7, 6 }, WorkflowArchiveLimits.Default));
    }
}
