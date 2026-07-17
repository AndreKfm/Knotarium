// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text.Json;
using Knotarium.Core.Domain;
using Knotarium.Features.Condition;
using Knotarium.Features.Nodes.Condition;
using Xunit;

namespace Knotarium.Tests.Nodes.Condition;

/// <summary>
/// Publish-time completeness gate (Phase 4): a condition is publishable with a valid `logic` graph OR
/// a usable legacy operator; otherwise it's incomplete and blocked. Mirrors the unbound-slot gate.
/// </summary>
public class ConditionPublishGateTests
{
    private static NodeDefinition Condition(string id, Dictionary<string, object> properties) =>
        new(new NodeId(id), "condition", properties);

    // Round-trips properties through JSON so values arrive as JsonElement, exactly as a publish request
    // deserializes them (object-typed dictionary values become JsonElement under System.Text.Json).
    private static NodeDefinition ConditionFromJson(string id, string propertiesJson)
    {
        var props = JsonSerializer.Deserialize<Dictionary<string, object>>(propertiesJson)!;
        return new NodeDefinition(new NodeId(id), "condition", props);
    }

    private const string ValidLogic =
        """{ "version": 1, "comb": "and", "cmps": [ { "id": "c1", "op": "eq", "a": { "kind": "lit", "type": "number", "value": 5 }, "b": { "kind": "lit", "type": "number", "value": 5 } } ] }""";

    [Fact]
    public void A_valid_logic_graph_is_complete()
    {
        var nodes = new[] { ConditionFromJson("n1", $$"""{ "logic": {{ValidLogic}} }""") };
        Assert.Empty(ConditionPublishGate.FindIncompleteConditions(nodes));
    }

    [Fact]
    public void A_legacy_operator_without_logic_is_complete()
    {
        var nodes = new[] { ConditionFromJson("n1", """{ "left": "{{ $variables.x }}", "operator": "Equal", "right": "5" }""") };
        Assert.Empty(ConditionPublishGate.FindIncompleteConditions(nodes));
    }

    [Fact]
    public void No_logic_and_no_operator_is_incomplete()
    {
        var nodes = new[] { Condition("n1", new Dictionary<string, object>()) };
        Assert.Equal(new[] { "n1" }, ConditionPublishGate.FindIncompleteConditions(nodes));
    }

    [Fact]
    public void An_empty_string_logic_and_empty_operator_is_incomplete()
    {
        var nodes = new[] { ConditionFromJson("n1", """{ "logic": "", "operator": "" }""") };
        Assert.Equal(new[] { "n1" }, ConditionPublishGate.FindIncompleteConditions(nodes));
    }

    [Fact]
    public void A_malformed_logic_blob_is_incomplete()
    {
        // version must be 1; this parses as JSON but fails schema validation → not a usable graph.
        var nodes = new[] { ConditionFromJson("n1", """{ "logic": { "version": 2, "comb": "and", "cmps": [] } }""") };
        Assert.Equal(new[] { "n1" }, ConditionPublishGate.FindIncompleteConditions(nodes));
    }

    [Fact]
    public void Non_condition_nodes_are_ignored()
    {
        var nodes = new[]
        {
            new NodeDefinition(new NodeId("log1"), "log", new Dictionary<string, object>()),
            ConditionFromJson("c1", $$"""{ "logic": {{ValidLogic}} }"""),
        };
        Assert.Empty(ConditionPublishGate.FindIncompleteConditions(nodes));
    }

    [Fact]
    public void Reports_every_incomplete_condition_in_order()
    {
        var nodes = new[]
        {
            Condition("bad1", new Dictionary<string, object>()),
            ConditionFromJson("ok", $$"""{ "logic": {{ValidLogic}} }"""),
            Condition("bad2", new Dictionary<string, object>()),
        };
        Assert.Equal(new[] { "bad1", "bad2" }, ConditionPublishGate.FindIncompleteConditions(nodes));
    }
}
