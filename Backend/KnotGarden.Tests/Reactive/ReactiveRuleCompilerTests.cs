using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KnotGarden.Core.Domain;
using KnotGarden.Core.Reactive;
using Xunit;

namespace KnotGarden.Tests.Reactive;

public class ReactiveRuleCompilerTests
{
    private static NodeDefinition Device(string id, string targetId) =>
        new(NodeId.Create(id), "externalDevice", new Dictionary<string, object>
        {
            ["targetId"] = new Dictionary<string, object> { ["value"] = targetId, ["label"] = targetId },
        });

    private static EdgeDefinition Wire(string id, string from, string evt, string to, string act) =>
        new(id, NodeId.Create(from), $"evt:{evt}", NodeId.Create(to), $"act:{act}");

    private static WorkflowDefinition Wf(IEnumerable<NodeDefinition> nodes, IEnumerable<EdgeDefinition> edges) =>
        new(new WorkflowDefinitionId("wf-1"), "wf", nodes.ToList(), edges.ToList());

    [Fact]
    public void Compiles_a_direct_cross_instance_wire_into_one_rule()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), Device("B", "siteB") },
            new[] { Wire("e1", "A", "VehicleRecognised", "B", "StartRecording") });

        var rules = ReactiveRuleCompiler.Compile(wf);

        var rule = Assert.Single(rules);
        Assert.Equal(new ReactiveSignalRef("siteA", "VehicleRecognised"), rule.Trigger);
        Assert.Equal(new ReactiveSignalRef("siteB", "StartRecording"), Assert.Single(rule.Effects));
    }

    [Fact]
    public void Groups_fan_out_under_one_trigger()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), Device("B", "siteB"), Device("C", "siteC") },
            new[]
            {
                Wire("e1", "A", "DoorForced", "B", "StartRecording"),
                Wire("e2", "A", "DoorForced", "C", "TriggerAlarm"),
            });

        var rule = Assert.Single(ReactiveRuleCompiler.Compile(wf));
        Assert.Equal("siteA", rule.Trigger.TargetId);
        Assert.Equal(2, rule.Effects.Count);
        Assert.Contains(new ReactiveSignalRef("siteB", "StartRecording"), rule.Effects);
        Assert.Contains(new ReactiveSignalRef("siteC", "TriggerAlarm"), rule.Effects);
    }

    [Fact]
    public void Fan_in_produces_distinct_rules_per_event()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), Device("B", "siteB") },
            new[]
            {
                Wire("e1", "A", "MotionDetected", "B", "StartRecording"),
                Wire("e2", "B", "DoorForced", "B", "StartRecording"),
            });

        var rules = ReactiveRuleCompiler.Compile(wf);
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r.Trigger == new ReactiveSignalRef("siteA", "MotionDetected"));
        Assert.Contains(rules, r => r.Trigger == new ReactiveSignalRef("siteB", "DoorForced"));
    }

    [Fact]
    public void Ignores_edges_that_are_not_device_pin_to_device_pin()
    {
        var logic = new NodeDefinition(NodeId.Create("L"), "condition", new Dictionary<string, object>());
        var wf = Wf(
            new[] { Device("A", "siteA"), Device("B", "siteB"), logic },
            new[]
            {
                // event-out -> a non-device node (Phase 3 territory): not compiled in Phase 2
                new EdgeDefinition("e1", NodeId.Create("A"), "evt:X", NodeId.Create("L"), "in"),
                // control-flow edge between non-pins
                new EdgeDefinition("e2", NodeId.Create("L"), "result", NodeId.Create("B"), "in"),
            });

        Assert.Empty(ReactiveRuleCompiler.Compile(wf));
    }

    [Fact]
    public void Skips_wires_when_a_device_has_no_target_picked()
    {
        var noTarget = new NodeDefinition(NodeId.Create("A"), "externalDevice", new Dictionary<string, object>());
        var wf = Wf(
            new[] { noTarget, Device("B", "siteB") },
            new[] { Wire("e1", "A", "X", "B", "Y") });

        Assert.Empty(ReactiveRuleCompiler.Compile(wf));
    }

    [Fact]
    public void Reads_targetId_from_a_JsonElement_object_value()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("""{ "value": "siteJson", "label": "Site JSON", "mode": "list" }""");
        var deviceA = new NodeDefinition(NodeId.Create("A"), "externalDevice", new Dictionary<string, object> { ["targetId"] = json });
        var wf = Wf(
            new[] { deviceA, Device("B", "siteB") },
            new[] { Wire("e1", "A", "Evt", "B", "Act") });

        var rule = Assert.Single(ReactiveRuleCompiler.Compile(wf));
        Assert.Equal("siteJson", rule.Trigger.TargetId);
    }

    [Fact]
    public void No_devices_yields_no_rules()
    {
        var n = new NodeDefinition(NodeId.Create("x"), "log", new Dictionary<string, object>());
        Assert.Empty(ReactiveRuleCompiler.Compile(Wf(new[] { n }, System.Array.Empty<EdgeDefinition>())));
    }
}
