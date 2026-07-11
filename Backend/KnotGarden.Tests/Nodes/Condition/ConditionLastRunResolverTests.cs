using System;
using System.Collections.Generic;
using System.Text.Json;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Condition;
using KnotGarden.Features.Nodes.Condition;
using Xunit;

namespace KnotGarden.Tests.Nodes.Condition;

/// <summary>
/// Last-run value resolution (Phase 5): resolves operand refs against a stored run without re-executing,
/// reusing the runtime expression evaluator. Covers hit (node output + variable), miss, nested path,
/// resolved-null vs missing, and the (removed) name-based masking — values are no longer masked (R5).
/// </summary>
public class ConditionLastRunResolverTests
{
    private static ExecutionInstance Run(
        Dictionary<string, object>? globals = null,
        params (string NodeId, Dictionary<string, object> Outputs)[] nodes)
    {
        var instance = new ExecutionInstance
        {
            Id = new ExecutionInstanceId(Guid.NewGuid()),
            WorkflowDefinitionId = new WorkflowDefinitionId("wf"),
            GlobalVariables = globals ?? new Dictionary<string, object>(),
        };
        foreach (var (nodeId, outputs) in nodes)
        {
            instance.NodeStates.Add(new NodeState { NodeId = new NodeId(nodeId), Outputs = outputs });
        }
        return instance;
    }

    // Outputs/globals arrive as JsonElement after persistence; round-trip to mirror that.
    private static object Json(string raw) => JsonSerializer.Deserialize<JsonElement>(raw);

    [Fact]
    public void Resolves_a_node_output_reference()
    {
        var run = Run(nodes: ("http", new Dictionary<string, object> { ["statusCode"] = Json("200") }));
        var values = ConditionLastRunResolver.Resolve(run, new[] { "{{ $node.http.output.statusCode }}" });

        var v = values["{{ $node.http.output.statusCode }}"];
        Assert.True(v.Found);
        Assert.False(v.Sensitive);
        Assert.Equal(200L, Convert.ToInt64(v.Value));
    }

    [Fact]
    public void Resolves_a_global_variable_reference()
    {
        var run = Run(globals: new Dictionary<string, object> { ["plan"] = Json("\"pro\"") });
        var values = ConditionLastRunResolver.Resolve(run, new[] { "{{ $variables.plan }}" });

        var v = values["{{ $variables.plan }}"];
        Assert.True(v.Found);
        Assert.Equal("pro", v.Value?.ToString());
    }

    [Fact]
    public void Navigates_a_nested_output_path()
    {
        var run = Run(nodes: ("http", new Dictionary<string, object> { ["body"] = Json("{ \"plan\": \"pro\" }") }));
        var values = ConditionLastRunResolver.Resolve(run, new[] { "{{ $node.http.output.body.plan }}" });

        Assert.True(values["{{ $node.http.output.body.plan }}"].Found);
        Assert.Equal("pro", values["{{ $node.http.output.body.plan }}"].Value?.ToString());
    }

    [Fact]
    public void A_missing_reference_is_not_found()
    {
        var run = Run(nodes: ("http", new Dictionary<string, object> { ["statusCode"] = Json("200") }));
        var values = ConditionLastRunResolver.Resolve(run, new[] { "{{ $node.http.output.nope }}", "{{ $variables.absent }}" });

        Assert.False(values["{{ $node.http.output.nope }}"].Found);
        Assert.Null(values["{{ $node.http.output.nope }}"].Value);
        Assert.False(values["{{ $variables.absent }}"].Found);
    }

    [Fact]
    public void A_present_output_that_is_null_is_found_with_null()
    {
        var run = Run(globals: new Dictionary<string, object> { ["maybe"] = Json("null") });
        var values = ConditionLastRunResolver.Resolve(run, new[] { "{{ $variables.maybe }}" });

        Assert.True(values["{{ $variables.maybe }}"].Found); // present, legitimately null
        Assert.Null(values["{{ $variables.maybe }}"].Value);
    }

    [Fact]
    public void Does_not_mask_by_field_name()
    {
        // Name-substring masking was removed (R5): secrets are never persisted to a run, so the trust
        // boundary already covers exposure; the substring mask only produced false positives. A field
        // merely NAMED like a secret now resolves to its real value, unmasked.
        var run = Run(nodes: ("auth", new Dictionary<string, object> { ["token"] = Json("\"abc\"") }));
        var values = ConditionLastRunResolver.Resolve(run, new[] { "{{ $node.auth.output.token }}" });

        var v = values["{{ $node.auth.output.token }}"];
        Assert.True(v.Found);
        Assert.False(v.Sensitive);
        Assert.Equal("abc", v.Value);
    }
}
