using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KnotGarden.Api.Services;
using KnotGarden.Features.Execution;
using KnotGarden.Features.Templates;
using KnotGarden.Features.Portability;
using KnotGarden.Core.Domain;
using Xunit;

namespace KnotGarden.Tests.Templates;

public class ParameterSubstitutionTests
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

    private static JsonElement Prop(WorkflowExportDocument document, string nodeId, string key)
    {
        // After substitution a property holds a boxed CLR scalar (number/bool/string) or a rebuilt
        // object/array. Serialize just that value to assert on the JSON shape it is stored as — this is
        // the same round-trip WorkflowVersionSerializer performs when persisting the workflow.
        var value = document.Content.Nodes.Single(n => n.Id.Value == nodeId).Properties[key];
        return JsonSerializer.SerializeToElement(value);
    }

    private static IReadOnlyDictionary<string, ParameterValue> Values(params (string Key, object? Boxed, string Text)[] entries)
        => entries.ToDictionary(e => e.Key, e => new ParameterValue(e.Boxed, e.Text), StringComparer.Ordinal);

    [Fact]
    public void WholeValue_string_token_is_replaced_with_the_value()
    {
        var doc = Doc(Node("n1", ("channel", "{{param:slack_channel}}")));

        var result = CredentialSlotModule.SubstituteParameters(doc, Values(("slack_channel", "#alerts", "#alerts")));

        Assert.Equal("#alerts", Prop(result, "n1", "channel").GetString());
    }

    [Fact]
    public void WholeValue_number_token_serializes_as_a_json_number_not_a_string()
    {
        var doc = Doc(Node("n1", ("intervalSeconds", "{{param:interval}}")));

        var result = CredentialSlotModule.SubstituteParameters(doc, Values(("interval", 30d, "30")));

        var prop = Prop(result, "n1", "intervalSeconds");
        Assert.Equal(JsonValueKind.Number, prop.ValueKind);
        Assert.Equal(30, prop.GetInt32());
    }

    [Fact]
    public void WholeValue_boolean_token_serializes_as_a_json_boolean()
    {
        var doc = Doc(Node("n1", ("enabled", "{{param:on}}")));

        var result = CredentialSlotModule.SubstituteParameters(doc, Values(("on", true, "true")));

        var prop = Prop(result, "n1", "enabled");
        Assert.Equal(JsonValueKind.True, prop.ValueKind);
    }

    [Fact]
    public void Embedded_token_is_interpolated_as_text()
    {
        var doc = Doc(Node("n1", ("url", "https://{{param:host}}/api/{{param:version}}")));

        var result = CredentialSlotModule.SubstituteParameters(
            doc, Values(("host", "example.com", "example.com"), ("version", 2d, "2")));

        Assert.Equal("https://example.com/api/2", Prop(result, "n1", "url").GetString());
    }

    [Theory]
    [InlineData("{{param:a}}{{param:b}}")] // two adjacent tokens → embedded, not whole-value
    [InlineData(" {{param:a}}")]            // leading space → embedded, not whole-value
    public void Two_tokens_or_padding_are_treated_as_embedded(string raw)
    {
        var doc = Doc(Node("n1", ("v", raw)));

        var result = CredentialSlotModule.SubstituteParameters(
            doc, Values(("a", 1d, "1"), ("b", 2d, "2")));

        // Embedded → result is a string (interpolated), never a typed scalar.
        Assert.Equal(JsonValueKind.String, Prop(result, "n1", "v").ValueKind);
    }

    [Fact]
    public void Token_nested_inside_a_json_element_object_is_substituted()
    {
        // Properties arriving from a deserialized .kgtpl are JsonElement; a token nested in an object/array
        // must still be reached by the dual-representation walk.
        var nested = JsonSerializer.Deserialize<JsonElement>(
            """{ "headers": { "Authorization": "Bearer {{param:token}}" }, "tags": ["{{param:env}}"] }""");
        var doc = Doc(Node("n1", ("config", nested)));

        var result = CredentialSlotModule.SubstituteParameters(
            doc, Values(("token", "abc", "abc"), ("env", "prod", "prod")));

        var config = Prop(result, "n1", "config");
        Assert.Equal("Bearer abc", config.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Equal("prod", config.GetProperty("tags")[0].GetString());
    }

    [Fact]
    public void Unknown_token_key_is_left_intact_and_reported()
    {
        var doc = Doc(Node("n1", ("v", "{{param:never_declared}}")));

        var result = CredentialSlotModule.SubstituteParameters(doc, Values(("other", "x", "x")));

        Assert.Equal("{{param:never_declared}}", Prop(result, "n1", "v").GetString());
        Assert.Equal(new[] { "never_declared" }, CredentialSlotModule.FindUnsubstitutedParameters(result));
    }

    [Fact]
    public void Substitution_recomputes_the_checksum()
    {
        var doc = Doc(Node("n1", ("channel", "{{param:c}}")));
        var before = doc.Manifest.Checksum;

        var result = CredentialSlotModule.SubstituteParameters(doc, Values(("c", "#x", "#x")));

        Assert.NotEqual(before, result.Manifest.Checksum);
        Assert.Equal(WorkflowVersionSerializer.ComputeChecksum(result.Content), result.Manifest.Checksum);
    }

    [Fact]
    public void Substitution_does_not_mutate_the_source_document()
    {
        var doc = Doc(Node("n1", ("channel", "{{param:c}}")));

        var first = CredentialSlotModule.SubstituteParameters(doc, Values(("c", "#first", "#first")));
        var second = CredentialSlotModule.SubstituteParameters(doc, Values(("c", "#second", "#second")));

        // The shared source is untouched, so a second substitution can't see the first's value.
        Assert.Equal("#first", Prop(first, "n1", "channel").GetString());
        Assert.Equal("#second", Prop(second, "n1", "channel").GetString());
    }
}
