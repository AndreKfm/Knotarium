// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Knotarium.Core.Domain;
using Knotarium.Core.Reactive;
using Xunit;

namespace Knotarium.Tests.Reactive;

public class ReactiveGraphValidatorTests
{
    private static NodeDefinition Device(string id, string? targetId) =>
        new(NodeId.Create(id), "externalDevice", targetId is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object> { ["targetId"] = new Dictionary<string, object> { ["value"] = targetId } });

    private static NodeDefinition Condition(string id) =>
        new(NodeId.Create(id), "condition", new Dictionary<string, object>
        {
            ["logic"] = JsonSerializer.Deserialize<JsonElement>("""{ "version":2, "root": { "kind":"cmp","id":"c","op":"exists","a": { "kind":"ref","type":"string","ref":"x" } } }"""),
        });

    private static EdgeDefinition E(string id, string from, string fromHandle, string to, string toHandle) =>
        new(id, NodeId.Create(from), fromHandle, NodeId.Create(to), toHandle);

    private static WorkflowDefinition Wf(IEnumerable<NodeDefinition> nodes, IEnumerable<EdgeDefinition> edges) =>
        new(new WorkflowDefinitionId("wf-1"), "wf", nodes.ToList(), edges.ToList());

    [Fact]
    public void A_clean_direct_graph_has_no_diagnostics()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), Device("B", "siteB") },
            new[] { E("e1", "A", "evt:X", "B", "act:Y") });

        Assert.Empty(ReactiveGraphValidator.Validate(wf));
    }

    [Fact]
    public void A_wired_device_without_a_target_is_a_blocking_error()
    {
        var wf = Wf(
            new[] { Device("A", null), Device("B", "siteB") },
            new[] { E("e1", "A", "evt:X", "B", "act:Y") });

        var diag = Assert.Single(ReactiveGraphValidator.Validate(wf));
        Assert.Equal(ReactiveDiagnosticSeverity.Error, diag.Severity);
        Assert.Equal(ReactiveGraphValidator.DeviceNoTargetCode, diag.Code);
        Assert.Equal("A", diag.NodeId);
        Assert.Single(ReactiveGraphValidator.FindBlocking(wf));
    }

    [Fact]
    public void A_device_targeted_only_on_the_action_side_still_flags_the_untargeted_event_side()
    {
        var wf = Wf(
            new[] { Device("A", null), Device("B", "siteB") },
            new[] { E("e1", "A", "evt:X", "B", "act:Y") });

        // A (event source) is untargeted and wired → error; B is fine.
        Assert.Contains(ReactiveGraphValidator.Validate(wf), d => d.NodeId == "A" && d.Code == ReactiveGraphValidator.DeviceNoTargetCode);
    }

    [Fact]
    public void An_untargeted_device_with_no_wires_is_not_nagged()
    {
        var wf = Wf(new[] { Device("A", null), Device("B", "siteB") }, System.Array.Empty<EdgeDefinition>());
        Assert.Empty(ReactiveGraphValidator.Validate(wf));
    }

    [Fact]
    public void An_event_wired_to_a_normal_node_is_a_live_imperative_trigger_not_a_dead_end()
    {
        // An event pin wired to an ordinary node (here a code node) starts an imperative run via the
        // device-event bridge, so it is not flagged as a dead end.
        var inlineCode = new NodeDefinition(NodeId.Create("IC"), "inlineCode", new Dictionary<string, object>());
        var wf = Wf(
            new[] { Device("A", "siteA"), inlineCode, Device("B", "siteB") },
            new[]
            {
                E("e1", "A", "evt:Motion", "IC", "in"),
                E("e2", "IC", "result", "B", "act:Y"),
            });

        Assert.DoesNotContain(ReactiveGraphValidator.Validate(wf), d => d.Code == ReactiveGraphValidator.DeadEndWireCode);
    }

    [Fact]
    public void A_condition_with_no_branch_wired_is_a_dead_end_warning()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), Condition("C"), Device("B", "siteB") },
            new[] { E("e1", "A", "evt:Motion", "C", "in") }); // no true/false branch out of C

        Assert.Contains(ReactiveGraphValidator.Validate(wf), d => d.Code == ReactiveGraphValidator.DeadEndWireCode);
    }

    [Fact]
    public void A_live_wire_through_a_condition_is_not_flagged()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), Condition("C"), Device("B", "siteB") },
            new[]
            {
                E("e1", "A", "evt:Motion", "C", "in"),
                E("e2", "C", "true", "B", "act:Record"),
            });

        Assert.Empty(ReactiveGraphValidator.Validate(wf));
    }

    [Fact]
    public void No_device_nodes_yields_no_diagnostics()
    {
        var n = new NodeDefinition(NodeId.Create("x"), "log", new Dictionary<string, object>());
        Assert.Empty(ReactiveGraphValidator.Validate(Wf(new[] { n }, System.Array.Empty<EdgeDefinition>())));
    }
}
