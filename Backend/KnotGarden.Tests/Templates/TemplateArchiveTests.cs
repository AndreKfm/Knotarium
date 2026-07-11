using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using KnotGarden.Api.Services;
using KnotGarden.Features.Execution;
using KnotGarden.Features.Portability;
using KnotGarden.Features.Templates;
using KnotGarden.Core.Domain;
using Xunit;

namespace KnotGarden.Tests.Templates;

public class TemplateArchiveTests
{
    private static WorkflowExportDocument SampleDocument()
    {
        var content = new WorkflowExportContent(
            new[] { new NodeDefinition(NodeId.Create("n1"), "log", new Dictionary<string, object> { ["message"] = "hi" }) },
            Array.Empty<EdgeDefinition>());
        var checksum = WorkflowVersionSerializer.ComputeChecksum(content);
        return new WorkflowExportDocument(
            new WorkflowExportManifest("wf", "WF", 1, "Published", null, checksum),
            content);
    }

    private static (byte[] Bytes, TemplateManifest Manifest, string WorkflowJson) BuildValid(int schemaVersion = TemplateFormat.SchemaVersion)
    {
        var doc = SampleDocument();
        var json = WorkflowVersionSerializer.Serialize(doc);
        var manifest = new TemplateManifest(
            "tpl_wf", "1.0.0", schemaVersion, "WF", "me", "desc",
            new[] { "t" }, "cat", null, "2026-01-01T00:00:00.0000000Z", "WF",
            WorkflowVersionSerializer.ComputeChecksum(doc.Content), Array.Empty<TemplateCredentialSlot>());
        return (TemplateArchiveCodec.Write(new TemplateArchive(manifest, json)), manifest, json);
    }

    private static byte[] RawZip(params (string Name, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return memory.ToArray();
    }

    [Fact]
    public void Write_then_read_round_trips()
    {
        var (bytes, manifest, json) = BuildValid();

        var archive = TemplateArchiveCodec.Read(bytes);

        Assert.Equal(manifest.TemplateId, archive.Manifest.TemplateId);
        Assert.Equal(json, archive.WorkflowDocumentJson);
        // ReadAndVerify accepts a consistent archive.
        TemplateWorkflowReader.ReadAndVerify(archive);
    }

    [Fact]
    public void Write_is_deterministic()
    {
        var (first, manifest, json) = BuildValid();
        var second = TemplateArchiveCodec.Write(new TemplateArchive(manifest, json));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Read_rejects_unexpected_entry()
    {
        var (_, manifest, json) = BuildValid();
        var bytes = RawZip(
            (TemplateFormat.ManifestEntryName, TemplateSerializer.SerializeManifest(manifest)),
            (TemplateFormat.WorkflowEntryName, json),
            ("sneaky.txt", "payload"));

        Assert.Throws<TemplateArchiveException>(() => TemplateArchiveCodec.Read(bytes));
    }

    [Fact]
    public void Read_rejects_duplicate_entries()
    {
        var (_, manifest, json) = BuildValid();
        var manifestJson = TemplateSerializer.SerializeManifest(manifest);
        var bytes = RawZip(
            (TemplateFormat.ManifestEntryName, manifestJson),
            (TemplateFormat.ManifestEntryName, manifestJson),
            (TemplateFormat.WorkflowEntryName, json));

        Assert.Throws<TemplateArchiveException>(() => TemplateArchiveCodec.Read(bytes));
    }

    [Fact]
    public void Read_rejects_missing_workflow()
    {
        var (_, manifest, _) = BuildValid();
        var bytes = RawZip((TemplateFormat.ManifestEntryName, TemplateSerializer.SerializeManifest(manifest)));

        Assert.Throws<TemplateArchiveException>(() => TemplateArchiveCodec.Read(bytes));
    }

    [Fact]
    public void Read_rejects_unsupported_schema_version()
    {
        var (bytes, _, _) = BuildValid(schemaVersion: 99);
        var ex = Assert.Throws<TemplateArchiveException>(() => TemplateArchiveCodec.Read(bytes));
        Assert.Contains("schema version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_rejects_corrupt_bytes()
    {
        Assert.Throws<TemplateArchiveException>(() => TemplateArchiveCodec.Read(new byte[] { 1, 2, 3, 4, 5 }));
    }

    [Fact]
    public void ReadAndVerify_rejects_checksum_mismatch()
    {
        var (_, manifest, json) = BuildValid();
        var tampered = manifest with { WorkflowChecksum = "deadbeef" };
        var bytes = TemplateArchiveCodec.Write(new TemplateArchive(tampered, json));

        var archive = TemplateArchiveCodec.Read(bytes);
        var ex = Assert.Throws<TemplateArchiveException>(() => TemplateWorkflowReader.ReadAndVerify(archive));
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadAndVerify_rejects_overly_deep_workflow_json()
    {
        var (_, manifest, _) = BuildValid();
        var deep = string.Concat(Enumerable.Repeat("[", TemplateFormat.MaxJsonDepth + 5))
                 + string.Concat(Enumerable.Repeat("]", TemplateFormat.MaxJsonDepth + 5));
        var archive = new TemplateArchive(manifest, deep);

        Assert.Throws<TemplateArchiveException>(() => TemplateWorkflowReader.ReadAndVerify(archive));
    }

    [Fact]
    public void ReadAndVerify_rejects_too_many_properties_on_a_node()
    {
        var props = new Dictionary<string, object>();
        for (var i = 0; i <= TemplateFormat.MaxPropertyCountPerNode; i++)
        {
            props["p" + i] = "v";
        }

        var content = new WorkflowExportContent(
            new[] { new NodeDefinition(NodeId.Create("n1"), "log", props) },
            Array.Empty<EdgeDefinition>());
        var doc = new WorkflowExportDocument(
            new WorkflowExportManifest("wf", "WF", 1, "Published", null, WorkflowVersionSerializer.ComputeChecksum(content)),
            content);
        var manifest = new TemplateManifest(
            "tpl_wf", "1.0.0", 1, "WF", "me", "d", Array.Empty<string>(), "cat", null,
            "2026-01-01T00:00:00.0000000Z", "WF", doc.Manifest.Checksum, Array.Empty<TemplateCredentialSlot>());

        var archive = new TemplateArchive(manifest, WorkflowVersionSerializer.Serialize(doc));
        Assert.Throws<TemplateArchiveException>(() => TemplateWorkflowReader.ReadAndVerify(archive));
    }
}
