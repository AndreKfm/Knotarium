// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Knotarium.Features.NodeEditor;

/// <summary>
/// Parses the sandbox's manifest.yaml and tests/cases.yaml drafts. Parse failures are
/// reported as failed test cases so the editor surfaces them like any other result.
/// </summary>
internal static class SandboxYamlParser
{
    public static ManifestDocument? ParseManifest(string manifestYaml, List<string> logs, List<NodeEditorTestCaseResult> cases)
    {
        try
        {
            var manifest = BuildDeserializer().Deserialize<ManifestDocument>(manifestYaml) ?? new ManifestDocument();
            if (manifest.Capabilities == null)
            {
                manifest.Capabilities = new List<string>();
            }

            if (string.IsNullOrWhiteSpace(manifest.Version))
            {
                manifest.Version = "1.0.0";
            }

            logs.Add($"[SANDBOX] Declared capabilities: {JsonSerializer.Serialize(manifest.Capabilities)}");
            return manifest;
        }
        catch (Exception ex)
        {
            logs.Add($"[SANDBOX] manifest.yaml parse failed: {ex.Message}");
            cases.Add(new NodeEditorTestCaseResult("Manifest parse", "fail", ex.Message));
            return null;
        }
    }

    public static List<TestCaseDocument>? ParseTests(string testsYaml, List<string> logs, List<NodeEditorTestCaseResult> cases)
    {
        if (string.IsNullOrWhiteSpace(testsYaml))
        {
            return new List<TestCaseDocument>();
        }

        try
        {
            var doc = BuildDeserializer().Deserialize<TestsDocument>(testsYaml);
            return doc?.Cases ?? new List<TestCaseDocument>();
        }
        catch
        {
            // The editor accepts either a { cases: [...] } document or a bare case list.
            try
            {
                var directCases = BuildDeserializer().Deserialize<List<TestCaseDocument>>(testsYaml);
                return directCases ?? new List<TestCaseDocument>();
            }
            catch (Exception ex)
            {
                logs.Add($"[SANDBOX] tests/cases.yaml parse failed: {ex.Message}");
                cases.Add(new NodeEditorTestCaseResult("Tests parse", "fail", ex.Message));
                return null;
            }
        }
    }

    private static IDeserializer BuildDeserializer()
    {
        return new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }
}
