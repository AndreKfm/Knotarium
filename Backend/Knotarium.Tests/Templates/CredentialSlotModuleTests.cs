// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Knotarium.Api.Services;
using Knotarium.Features.Execution;
using Knotarium.Features.Portability;
using Knotarium.Core.Domain;
using Xunit;

namespace Knotarium.Tests.Templates;

public class CredentialSlotModuleTests
{
    private static NodeDefinition Node(string id, params (string Key, object Value)[] props)
        => new(NodeId.Create(id), "log", props.ToDictionary(p => p.Key, p => p.Value));

    private static WorkflowExportDocument Doc(params NodeDefinition[] nodes)
    {
        var content = new WorkflowExportContent(nodes, Array.Empty<EdgeDefinition>());
        var checksum = WorkflowVersionSerializer.ComputeChecksum(content);
        return new WorkflowExportDocument(
            new WorkflowExportManifest("wf-1", "WF", 1, "Published", null, checksum),
            content);
    }

    private static string? PropString(WorkflowExportDocument document, string nodeId, string key)
    {
        var node = document.Content.Nodes.Single(n => n.Id.Value == nodeId);
        var value = node.Properties[key];
        return value is JsonElement element ? element.GetString() : value as string;
    }

    [Fact]
    public void Extract_replaces_known_credential_id_with_slot_placeholder()
    {
        var doc = Doc(Node("n1", ("apiKey", "cred-abc")));
        var idToName = new Dictionary<string, string> { ["cred-abc"] = "Weather API" };

        var result = CredentialSlotModule.ExtractIdsToSlots(doc, idToName);

        var slot = Assert.Single(result.Slots);
        Assert.Equal("weather-api", slot.Slot);
        Assert.Equal("Weather API", slot.DisplayName);
        Assert.Equal("cred-abc", slot.SourceCredentialId);
        Assert.Equal("slot:weather-api", PropString(result.Document, "n1", "apiKey"));
        Assert.Contains("n1.apiKey", result.RewrittenPaths);
    }

    [Fact]
    public void Extract_leaves_credential_id_shaped_string_untouched_when_not_a_known_credential()
    {
        // A GUID-shaped value that is NOT in the credentials table must not be portabilized.
        var notACredential = Guid.Empty.ToString();
        var doc = Doc(Node("n1", ("note", notACredential)));
        var idToName = new Dictionary<string, string> { ["cred-real"] = "Real" };

        var result = CredentialSlotModule.ExtractIdsToSlots(doc, idToName);

        Assert.Empty(result.Slots);
        Assert.Empty(result.RewrittenPaths);
        Assert.Equal(notACredential, PropString(result.Document, "n1", "note"));
    }

    [Fact]
    public void Extract_maps_many_references_of_one_credential_to_a_single_slot()
    {
        var doc = Doc(
            Node("n1", ("apiKey", "cred-x")),
            Node("n2", ("token", "cred-x")),
            Node("n3", ("auth", "cred-x")));
        var idToName = new Dictionary<string, string> { ["cred-x"] = "Shared" };

        var result = CredentialSlotModule.ExtractIdsToSlots(doc, idToName);

        Assert.Single(result.Slots);
        Assert.Equal(3, result.RewrittenPaths.Count);
        Assert.All(new[] { "n1", "n2", "n3" }, id => Assert.Equal("slot:shared", PropString(result.Document, id, id == "n1" ? "apiKey" : id == "n2" ? "token" : "auth")));
    }

    [Fact]
    public void Extract_suffixes_slot_keys_for_two_credentials_with_the_same_name()
    {
        var doc = Doc(Node("n1", ("a", "cred-1")), Node("n2", ("b", "cred-2")));
        var idToName = new Dictionary<string, string> { ["cred-1"] = "Camera API", ["cred-2"] = "Camera API" };

        var result = CredentialSlotModule.ExtractIdsToSlots(doc, idToName);

        var slots = result.Slots.Select(s => s.Slot).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "camera-api", "camera-api-2" }, slots);
    }

    [Fact]
    public void Extract_treats_case_insensitive_name_collisions_as_collisions()
    {
        var doc = Doc(Node("n1", ("a", "cred-1")), Node("n2", ("b", "cred-2")));
        var idToName = new Dictionary<string, string> { ["cred-1"] = "Production Camera", ["cred-2"] = "production-camera" };

        var result = CredentialSlotModule.ExtractIdsToSlots(doc, idToName);

        var slots = result.Slots.Select(s => s.Slot).ToList();
        Assert.Equal(2, slots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("Weather API", "weather-api")]
    [InlineData("  Spaces  &  Symbols!! ", "spaces-symbols")]
    [InlineData("123 Numbers First", "c-123-numbers-first")]
    [InlineData("slot:Sneaky", "slot-sneaky")]
    [InlineData("???", "credential")]
    public void Extract_generates_valid_slot_keys(string credentialName, string expectedSlot)
    {
        var doc = Doc(Node("n1", ("a", "cred-1")));
        var idToName = new Dictionary<string, string> { ["cred-1"] = credentialName };

        var result = CredentialSlotModule.ExtractIdsToSlots(doc, idToName);

        var slot = Assert.Single(result.Slots);
        Assert.Equal(expectedSlot, slot.Slot);
        Assert.Matches(CredentialSlotTokens.SlotKeyPattern, slot.Slot);
    }

    [Fact]
    public void All_generated_slot_keys_match_the_grammar()
    {
        var idToName = new Dictionary<string, string>();
        var nodes = new List<NodeDefinition>();
        foreach (var name in new[] { "A", "@@@", "Mixed Café Ñ", "UPPER", "x", "9lives" })
        {
            var id = "cred-" + nodes.Count;
            idToName[id] = name;
            nodes.Add(Node("node-" + nodes.Count, ("k", id)));
        }

        var result = CredentialSlotModule.ExtractIdsToSlots(Doc(nodes.ToArray()), idToName);

        Assert.All(result.Slots, slot => Assert.Matches(CredentialSlotTokens.SlotKeyPattern, slot.Slot));
    }

    [Fact]
    public void Rebind_replaces_slot_placeholder_with_bound_credential_id()
    {
        var doc = Doc(Node("n1", ("apiKey", "slot:weather-api")));
        var bindings = new Dictionary<string, string> { ["weather-api"] = "cred-live" };

        var result = CredentialSlotModule.RebindSlotsToIds(doc, bindings);

        Assert.Equal("cred-live", PropString(result.Document, "n1", "apiKey"));
        Assert.Equal(new[] { "weather-api" }, result.ReboundSlots);
        Assert.Empty(result.UnboundSlots);
    }

    [Fact]
    public void Rebind_reports_unbound_slots_and_leaves_placeholder()
    {
        var doc = Doc(Node("n1", ("apiKey", "slot:weather-api")));

        var result = CredentialSlotModule.RebindSlotsToIds(doc, new Dictionary<string, string>());

        Assert.Equal("slot:weather-api", PropString(result.Document, "n1", "apiKey"));
        Assert.Equal(new[] { "weather-api" }, result.UnboundSlots);
    }

    [Fact]
    public void Extract_then_rebind_round_trips_to_the_original()
    {
        var original = Doc(
            Node("n1", ("apiKey", "cred-a")),
            Node("n2", ("token", "cred-b"), ("note", "leave me")));
        var idToName = new Dictionary<string, string> { ["cred-a"] = "Alpha", ["cred-b"] = "Beta" };

        var extracted = CredentialSlotModule.ExtractIdsToSlots(original, idToName);
        var bindings = extracted.Slots.ToDictionary(s => s.Slot, s => s.SourceCredentialId);
        var rebound = CredentialSlotModule.RebindSlotsToIds(extracted.Document, bindings);

        Assert.Equal(
            WorkflowVersionSerializer.Serialize(original),
            WorkflowVersionSerializer.Serialize(rebound.Document));
    }

    [Fact]
    public void FindUnboundSlots_lists_remaining_placeholders()
    {
        var doc = Doc(Node("n1", ("a", "slot:one")), Node("n2", ("b", "slot:two"), ("c", "cred-x")));

        var unbound = CredentialSlotModule.FindUnboundSlots(doc);

        Assert.Equal(new[] { "one", "two" }, unbound);
    }

    // ── R9: Condition `logic` portability ────────────────────────────────────
    // The condition `logic` blob is a nested object whose operands are VARIABLE refs (not credential
    // slots). The portability walk must carry it through untouched (extract/rebind), yet still descend
    // into it for parameter templating like any other property — proving it neither chokes nor is special.

    private const string ConditionLogicJson =
        """{"version":1,"comb":"and","cmps":[{"id":"c1","op":"eq","a":{"kind":"ref","type":"number","ref":{"__type":"variable_ref","variableName":"plan"}},"b":{"kind":"lit","type":"number","value":5}}]}""";

    private static JsonElement Logic(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static string PropRawJson(WorkflowExportDocument document, string nodeId, string key)
    {
        var value = document.Content.Nodes.Single(n => n.Id.Value == nodeId).Properties[key];
        return value is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(value);
    }

    [Fact]
    public void Condition_logic_survives_credential_extract_and_rebind_unchanged()
    {
        var original = Doc(new NodeDefinition(NodeId.Create("cond"), "condition",
            new Dictionary<string, object> { ["apiKey"] = "cred-a", ["logic"] = Logic(ConditionLogicJson) }));
        var idToName = new Dictionary<string, string> { ["cred-a"] = "Alpha" };

        var extracted = CredentialSlotModule.ExtractIdsToSlots(original, idToName);

        // The sibling credential is slotted; the logic blob is byte-for-byte untouched (refs ≠ slots).
        Assert.Equal("slot:alpha", PropString(extracted.Document, "cond", "apiKey"));
        Assert.Equal(ConditionLogicJson, PropRawJson(extracted.Document, "cond", "logic"));

        var bindings = extracted.Slots.ToDictionary(s => s.Slot, s => s.SourceCredentialId);
        var rebound = CredentialSlotModule.RebindSlotsToIds(extracted.Document, bindings);

        // Strongest statement: whole-document round-trip is identical — logic AND credentials restored.
        Assert.Equal(
            WorkflowVersionSerializer.Serialize(original),
            WorkflowVersionSerializer.Serialize(rebound.Document));
    }

    [Fact]
    public void V2_nested_condition_logic_survives_extract_and_rebind_unchanged()
    {
        // Phase 8: a v2 tree (group → cmp + not(cmp), with a ref operand in a deep leaf) is a deeper
        // nested object than v1, but the portability walk treats it the same — refs aren't slots, so the
        // whole tree round-trips byte-identical while a sibling credential still slots/rebinds.
        const string v2 =
            """{"version":2,"root":{"kind":"group","id":"g1","op":"and","children":[{"kind":"cmp","id":"c1","op":"eq","a":{"kind":"ref","type":"number","ref":{"__type":"variable_ref","variableName":"plan"}},"b":{"kind":"lit","type":"number","value":5}},{"kind":"not","id":"n1","child":{"kind":"cmp","id":"c2","op":"gt","a":{"kind":"lit","type":"number","value":1},"b":{"kind":"lit","type":"number","value":2}}}]}}""";
        var original = Doc(new NodeDefinition(NodeId.Create("cond"), "condition",
            new Dictionary<string, object> { ["apiKey"] = "cred-a", ["logic"] = Logic(v2) }));
        var idToName = new Dictionary<string, string> { ["cred-a"] = "Alpha" };

        var extracted = CredentialSlotModule.ExtractIdsToSlots(original, idToName);
        Assert.Equal("slot:alpha", PropString(extracted.Document, "cond", "apiKey"));
        Assert.Equal(v2, PropRawJson(extracted.Document, "cond", "logic"));

        var bindings = extracted.Slots.ToDictionary(s => s.Slot, s => s.SourceCredentialId);
        var rebound = CredentialSlotModule.RebindSlotsToIds(extracted.Document, bindings);
        Assert.Equal(
            WorkflowVersionSerializer.Serialize(original),
            WorkflowVersionSerializer.Serialize(rebound.Document));
    }

    [Fact]
    public void Parameter_substitution_reaches_into_condition_logic()
    {
        // A {{param:…}} inside a logic literal is substituted like any other property leaf — the walk
        // descends into `logic` (doesn't skip or choke on the nested structure).
        const string templated =
            """{"version":1,"comb":"and","cmps":[{"id":"c1","op":"eq","a":{"kind":"lit","type":"string","value":"{{param:env}}"},"b":{"kind":"lit","type":"string","value":"prod"}}]}""";
        var doc = Doc(new NodeDefinition(NodeId.Create("cond"), "condition",
            new Dictionary<string, object> { ["logic"] = Logic(templated) }));
        var values = new Dictionary<string, ParameterValue> { ["env"] = new("prod", "prod") };

        var result = CredentialSlotModule.SubstituteParameters(doc, values);

        var logic = JsonDocument.Parse(PropRawJson(result, "cond", "logic")).RootElement;
        var aValue = logic.GetProperty("cmps")[0].GetProperty("a").GetProperty("value").GetString();
        Assert.Equal("prod", aValue);
        Assert.Empty(CredentialSlotModule.FindUnsubstitutedParameters(result));
    }
}
