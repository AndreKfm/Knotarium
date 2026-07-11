using System.Collections.Generic;
using System.Linq;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Core.Reactive;
using Xunit;

namespace Knotarium.Tests.Reactive;

/// <summary>
/// CompileSignalTriggers — device inbound signal pins (events AND incoming actions) wired to ordinary
/// (non-device) nodes, which bridge the device bus into the imperative run engine.
/// </summary>
public class ReactiveEventTriggerCompilerTests
{
    private static NodeDefinition Device(string id, string targetId) =>
        new(NodeId.Create(id), "externalDevice", new Dictionary<string, object>
        {
            ["targetId"] = new Dictionary<string, object> { ["value"] = targetId },
        });

    private static NodeDefinition Node(string id, string type) =>
        new(NodeId.Create(id), type, new Dictionary<string, object>());

    private static EdgeDefinition Edge(string id, string from, string output, string to, string input) =>
        new(id, NodeId.Create(from), output, NodeId.Create(to), input);

    private static WorkflowDefinition Wf(IEnumerable<NodeDefinition> nodes, IEnumerable<EdgeDefinition> edges) =>
        new(new WorkflowDefinitionId("wf-1"), "wf", nodes.ToList(), edges.ToList());

    [Fact]
    public void Event_pin_wired_to_a_normal_node_becomes_an_event_trigger()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), Node("L", "log") },
            new[] { Edge("e1", "A", "evt:1:started", "L", "in") });

        var trigger = Assert.Single(ReactiveRuleCompiler.CompileSignalTriggers(wf));
        Assert.Equal(ExternalSignalKind.Event, trigger.Kind);
        Assert.Equal("siteA", trigger.TargetId);
        Assert.Equal("1:started", trigger.SignalType);
        Assert.Equal("L", trigger.EntryNodeId);
    }

    [Fact]
    public void Incoming_action_pin_wired_to_a_normal_node_becomes_an_action_trigger()
    {
        // The device block exposes incoming actions as SOURCE pins (act:<type> on the output side) so a
        // graph can react to actions raised by the device — same bridge as events, Kind=Action.
        var wf = Wf(
            new[] { Device("A", "siteA"), Node("L", "log") },
            new[] { Edge("e1", "A", "act:CameraCycle", "L", "in") });

        var trigger = Assert.Single(ReactiveRuleCompiler.CompileSignalTriggers(wf));
        Assert.Equal(ExternalSignalKind.Action, trigger.Kind);
        Assert.Equal("siteA", trigger.TargetId);
        Assert.Equal("CameraCycle", trigger.SignalType);
        Assert.Equal("L", trigger.EntryNodeId);
    }

    [Fact]
    public void Legacy_device_to_device_event_wire_yields_no_signal_trigger()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), Device("B", "siteB") },
            new[] { Edge("e1", "A", "evt:1:started", "B", "act:Record") });

        Assert.Empty(ReactiveRuleCompiler.CompileSignalTriggers(wf));
    }

    [Fact]
    public void An_event_pin_consumed_by_a_reactive_rule_is_not_also_a_signal_trigger()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), Device("B", "siteB"), Node("L", "log") },
            new[]
            {
                Edge("e1", "A", "evt:1:started", "B", "act:Record"),
                Edge("e2", "A", "evt:1:started", "L", "in"),
            });

        Assert.Single(ReactiveRuleCompiler.Compile(wf));
        Assert.Empty(ReactiveRuleCompiler.CompileSignalTriggers(wf));
    }

    [Fact]
    public void A_device_without_a_target_yields_no_signal_trigger()
    {
        var wf = Wf(
            new[] { Node("A", "externalDevice"), Node("L", "log") },
            new[] { Edge("e1", "A", "act:CameraCycle", "L", "in") });

        Assert.Empty(ReactiveRuleCompiler.CompileSignalTriggers(wf));
    }

    [Fact]
    public void Mixed_event_and_action_pins_each_produce_their_own_trigger()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), Node("L1", "log"), Node("L2", "log") },
            new[]
            {
                Edge("e1", "A", "evt:1:started", "L1", "in"),
                Edge("e2", "A", "act:CameraCycle", "L2", "in"),
            });

        var triggers = ReactiveRuleCompiler.CompileSignalTriggers(wf);
        Assert.Equal(2, triggers.Count);
        Assert.Contains(triggers, t => t.Kind == ExternalSignalKind.Event && t.SignalType == "1:started" && t.EntryNodeId == "L1");
        Assert.Contains(triggers, t => t.Kind == ExternalSignalKind.Action && t.SignalType == "CameraCycle" && t.EntryNodeId == "L2");
    }
}
