// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using Knotarium.Features.Templates;
using Xunit;

namespace Knotarium.Tests.Templates;

public class TemplateManifestParametersTests
{
    private static TemplateManifest ManifestWith(params TemplateParameter[] parameters)
        => new(
            "tpl_x", "1.0.0", TemplateFormat.SchemaVersion, "Name", "Author", "Desc",
            new[] { "tag" }, "cat", null, "2026-01-01T00:00:00.0000000Z", "Source",
            "checksum", Array.Empty<TemplateCredentialSlot>())
        {
            Parameters = parameters,
        };

    [Fact]
    public void Parameters_round_trip_through_the_serializer()
    {
        var manifest = ManifestWith(
            new TemplateParameter("interval", "Interval", "How often", "number", null, "60", false),
            new TemplateParameter("mode", "Mode", null, "enum", new[] { "fast", "slow" }, "fast", false));

        var back = TemplateSerializer.DeserializeManifest(TemplateSerializer.SerializeManifest(manifest));

        Assert.Equal(2, back.Parameters.Count);
        Assert.Equal("interval", back.Parameters[0].Key);
        Assert.Equal("number", back.Parameters[0].Type);
        Assert.Equal(new[] { "fast", "slow" }, back.Parameters[1].Options);
    }

    [Fact]
    public void A_v1_manifest_without_a_parameters_field_reads_as_an_empty_list()
    {
        // A pre-parameters .kgtpl (schemaVersion 1) has no "parameters" property at all.
        const string legacyJson = """
        {
          "templateId": "tpl_x",
          "templateVersion": "1.0.0",
          "schemaVersion": 1,
          "name": "Name",
          "author": "Author",
          "description": "Desc",
          "tags": ["tag"],
          "category": "cat",
          "minEngineVersion": null,
          "createdAtUtc": "2026-01-01T00:00:00.0000000Z",
          "sourceWorkflowName": "Source",
          "workflowChecksum": "checksum",
          "credentialSlots": []
        }
        """;

        var manifest = TemplateSerializer.DeserializeManifest(legacyJson);

        Assert.NotNull(manifest.Parameters);
        Assert.Empty(manifest.Parameters);
    }
}
