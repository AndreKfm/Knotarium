// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Knotarium.Features.Templates;
using Knotarium.Tests.Compiler;
using Xunit;

namespace Knotarium.Tests.Templates;

public class BuiltInTemplateGalleryTests
{
    // Resolve the shipped, reviewable sources from the repo so the test validates the real starter
    // templates (their hand-authored JSON, checksums, and that they pack + verify cleanly).
    private static string ShippedSourcesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Knotarium.Api", "Templates", "Sources");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Knotarium.Api/Templates/Sources from the test output.");
    }

    private static BuiltInTemplateGallery Gallery() => new(ShippedSourcesDirectory());

    [Fact]
    public async Task Lists_the_shipped_starter_templates()
    {
        var templates = await Gallery().ListAsync();

        Assert.NotEmpty(templates);
        Assert.Contains(templates, t => t.TemplateId == "tpl_starter-hello-world");
        Assert.All(templates, t => Assert.Equal(TemplateFormat.SchemaVersion, t.Manifest.SchemaVersion));
    }

    [Fact]
    public async Task Each_shipped_template_packs_verifies_and_is_supported()
    {
        var gallery = Gallery();
        var checker = new TemplateCompatibilityChecker(TemplateTestFactory.Compiler());

        foreach (var template in await gallery.ListAsync())
        {
            var bytes = await gallery.GetArchiveBytesAsync(template.TemplateId);
            Assert.NotNull(bytes);

            // Packs to a valid archive whose checksum matches its contents …
            var archive = TemplateArchiveCodec.Read(bytes!);
            var document = TemplateWorkflowReader.ReadAndVerify(archive);

            // … and the workflow actually compiles on this engine.
            var compatibility = await checker.AssessAsync(document, archive.Manifest.MinEngineVersion);
            Assert.True(compatibility.Supported, $"{template.TemplateId} should be runnable: {string.Join("; ", compatibility.Warnings)}");
        }
    }

    [Fact]
    public async Task Packing_is_deterministic()
    {
        var gallery = Gallery();
        var id = (await gallery.ListAsync()).First().TemplateId;

        var first = await gallery.GetArchiveBytesAsync(id);
        var second = await gallery.GetArchiveBytesAsync(id);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Unknown_template_id_returns_null()
    {
        Assert.Null(await Gallery().GetArchiveBytesAsync("tpl_does-not-exist"));
        Assert.Null(await Gallery().GetAsync("tpl_does-not-exist"));
    }
}
