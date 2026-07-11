using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KnotGarden.Core.Domain;
using KnotGarden.Core.Reactive;
using Xunit;

namespace KnotGarden.Tests.Reactive;

// Phase 3 — Condition nodes on the wire. Traversal carries a guard per followed branch.
public class ReactiveRuleCompilerConditionTests
{
    private static NodeDefinition Device(string id, string targetId) =>
        new(NodeId.Create(id), "externalDevice", new Dictionary<string, object>
        {
            ["targetId"] = new Dictionary<string, object> { ["value"] = targetId },
        });

    private static NodeDefinition Condition(string id) =>
        new(NodeId.Create(id), "condition", new Dictionary<string, object>
        {
            ["logic"] = JsonSerializer.Deserialize<JsonElement>("""{ "version": 2, "root": { "kind": "cmp", "id": "c", "op": "exists", "a": { "kind": "ref", "type": "string", "ref": "plate" } } }"""),
        });

    private static EdgeDefinition E(string id, string from, string fromHandle, string to, string toHandle) =>
        new(id, NodeId.Create(from), fromHandle, NodeId.Create(to), toHandle);

    private static WorkflowDefinition Wf(IEnumerable<NodeDefinition> nodes, IEnumerable<EdgeDefinition> edges) =>
        new(new WorkflowDefinitionId("wf-1"), "wf", nodes.ToList(), edges.ToList());

    [Fact]
    public void Event_through_condition_true_branch_carries_one_true_guard()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), Condition("C"), Device("B", "siteB") },
            new[]
            {
                E("e1", "A", "evt:VehicleRecognised", "C", "in"),
                E("e2", "C", "true", "B", "act:StartRecording"),
            });

        var rule = Assert.Single(ReactiveRuleCompiler.Compile(wf));
        Assert.Equal(new ReactiveSignalRef("siteA", "VehicleRecognised"), rule.Trigger);
        Assert.Equal(new ReactiveSignalRef("siteB", "StartRecording"), Assert.Single(rule.Effects));
        var guard = Assert.Single(rule.Guards);
        Assert.Equal("C", guard.SourceNodeId);
        Assert.True(guard.ExpectTrue);
    }

    [Fact]
    public void True_and_false_branches_become_two_rules_with_opposite_guards()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), Condition("C"), Device("B", "siteB"), Device("D", "siteD") },
            new[]
            {
                E("e1", "A", "evt:Plate", "C", "in"),
                E("e2", "C", "true", "B", "act:Allow"),
                E("e3", "C", "false", "D", "act:Alarm"),
            });

        var rules = ReactiveRuleCompiler.Compile(wf);
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r.Guards.Single().ExpectTrue && r.Effects.Single() == new ReactiveSignalRef("siteB", "Allow"));
        Assert.Contains(rules, r => !r.Guards.Single().ExpectTrue && r.Effects.Single() == new ReactiveSignalRef("siteD", "Alarm"));
    }

    [Fact]
    public void Condition_true_fan_out_is_one_rule_many_effects_under_the_same_guard()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), Condition("C"), Device("B", "siteB"), Device("D", "siteD") },
            new[]
            {
                E("e1", "A", "evt:Plate", "C", "in"),
                E("e2", "C", "true", "B", "act:Record"),
                E("e3", "C", "true", "D", "act:Alarm"),
            });

        var rule = Assert.Single(ReactiveRuleCompiler.Compile(wf));
        Assert.Single(rule.Guards);
        Assert.Equal(2, rule.Effects.Count);
    }

    [Fact]
    public void Direct_wire_has_no_guards()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), Device("B", "siteB") },
            new[] { E("e1", "A", "evt:X", "B", "act:Y") });

        var rule = Assert.Single(ReactiveRuleCompiler.Compile(wf));
        Assert.Empty(rule.Guards);
    }

    [Fact]
    public void Condition_without_logic_property_drops_the_path()
    {
        var bareCondition = new NodeDefinition(NodeId.Create("C"), "condition", new Dictionary<string, object>());
        var wf = Wf(
            new[] { Device("A", "siteA"), bareCondition, Device("B", "siteB") },
            new[]
            {
                E("e1", "A", "evt:X", "C", "in"),
                E("e2", "C", "true", "B", "act:Y"),
            });

        Assert.Empty(ReactiveRuleCompiler.Compile(wf));
    }

    [Fact]
    public void Inline_code_on_the_path_stops_traversal()
    {
        var inlineCode = new NodeDefinition(NodeId.Create("IC"), "inlineCode", new Dictionary<string, object>());
        var wf = Wf(
            new[] { Device("A", "siteA"), inlineCode, Device("B", "siteB") },
            new[]
            {
                E("e1", "A", "evt:X", "IC", "in"),
                E("e2", "IC", "result", "B", "act:Y"),
            });

        // Inline Code on the wire is unsupported (heavy Roslyn on the bus thread) — the path doesn't compile.
        Assert.Empty(ReactiveRuleCompiler.Compile(wf));
    }
}
