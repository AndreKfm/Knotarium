using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Knotarium.Core.Domain;
using Knotarium.Core.Reactive;
using Xunit;

namespace Knotarium.Tests.Reactive;

// Phase 3b — Set Variable(s) transforms on the wire.
public class ReactiveRuleCompilerTransformTests
{
    private static NodeDefinition Device(string id, string targetId) =>
        new(NodeId.Create(id), "externalDevice", new Dictionary<string, object>
        {
            ["targetId"] = new Dictionary<string, object> { ["value"] = targetId },
        });

    private static NodeDefinition SetVars(string id, string variablesJson) =>
        new(NodeId.Create(id), "setVariables", new Dictionary<string, object>
        {
            ["variables"] = JsonSerializer.Deserialize<JsonElement>(variablesJson),
        });

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
    public void Event_through_set_variables_carries_a_transform_step()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), SetVars("S", """[ { "name":"flag", "value":"on" } ]"""), Device("B", "siteB") },
            new[]
            {
                E("e1", "A", "evt:Motion", "S", "in"),
                E("e2", "S", "result", "B", "act:Record"),
            });

        var rule = Assert.Single(ReactiveRuleCompiler.Compile(wf));
        Assert.Equal(new ReactiveSignalRef("siteB", "Record"), Assert.Single(rule.Effects));
        Assert.Empty(rule.Guards);
        var transform = Assert.IsType<ReactiveTransform>(Assert.Single(rule.Steps));
        Assert.Equal("S", transform.SourceNodeId);
        var assignment = Assert.Single(transform.Assignments);
        Assert.Equal("flag", assignment.Name);
    }

    [Fact]
    public void Transform_then_condition_preserves_path_order_in_steps()
    {
        var wf = Wf(
            new[] { Device("A", "siteA"), SetVars("S", """[ { "name":"x", "value":"1" } ]"""), Condition("C"), Device("B", "siteB") },
            new[]
            {
                E("e1", "A", "evt:Motion", "S", "in"),
                E("e2", "S", "result", "C", "in"),
                E("e3", "C", "true", "B", "act:Record"),
            });

        var rule = Assert.Single(ReactiveRuleCompiler.Compile(wf));
        Assert.Equal(2, rule.Steps.Count);
        Assert.IsType<ReactiveTransform>(rule.Steps[0]);
        var guard = Assert.IsType<ReactiveGuard>(rule.Steps[1]);
        Assert.Equal("C", guard.SourceNodeId);
        Assert.True(guard.ExpectTrue);
    }

    [Fact]
    public void Inline_code_on_the_path_still_stops_traversal()
    {
        var inlineCode = new NodeDefinition(NodeId.Create("IC"), "inlineCode", new Dictionary<string, object>());
        var wf = Wf(
            new[] { Device("A", "siteA"), inlineCode, Device("B", "siteB") },
            new[]
            {
                E("e1", "A", "evt:Motion", "IC", "in"),
                E("e2", "IC", "result", "B", "act:Record"),
            });

        Assert.Empty(ReactiveRuleCompiler.Compile(wf));
    }

    [Fact]
    public void Single_set_variable_node_is_also_a_transform()
    {
        var setVar = new NodeDefinition(NodeId.Create("S"), "setVariable", new Dictionary<string, object>
        {
            ["variableName"] = "flag",
            ["value"] = "on",
        });
        var wf = Wf(
            new[] { Device("A", "siteA"), setVar, Device("B", "siteB") },
            new[]
            {
                E("e1", "A", "evt:Motion", "S", "in"),
                E("e2", "S", "result", "B", "act:Record"),
            });

        var rule = Assert.Single(ReactiveRuleCompiler.Compile(wf));
        var transform = Assert.IsType<ReactiveTransform>(Assert.Single(rule.Steps));
        Assert.Equal("flag", Assert.Single(transform.Assignments).Name);
    }
}
