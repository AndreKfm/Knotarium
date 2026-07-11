using System;
using System.Collections.Generic;
using System.Text.Json;
using Knotarium.Core.Domain;
using Knotarium.Features.Execution;
using Xunit;

namespace Knotarium.Tests.Nodes.Condition;

/// <summary>
/// D7 resolution-parity (R3). The conformance fixture (B2) proves the <em>evaluator</em> agrees FE⇄BE
/// given identical resolved inputs; it says nothing about the <em>resolution layer</em>. The Condition
/// task resolves its own refs via <see cref="IWorkflowState.TryResolveVariable"/> (so a miss becomes
/// RESOLUTION_FAILED rather than collapsing to null), while the rest of the workflow resolves the same
/// <c>variable_ref</c> via <c>GetVariable&lt;object&gt;</c>. These tests pin the invariant that, for a
/// <b>found</b> ref, both paths yield the bit-identical value on the very same projection — a condition
/// can never see a different value for a reference than the node feeding it would.
/// </summary>
public class ConditionResolutionParityTests
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

    // Values arrive as JsonElement after persistence; round-trip so the test mirrors a real run.
    private static object Json(string raw) => JsonSerializer.Deserialize<JsonElement>(raw);

    [Fact]
    public void Direct_global_resolves_identically_on_both_paths()
    {
        var proj = new WorkflowExecutor.WorkflowStateProjection(
            Run(globals: new Dictionary<string, object> { ["plan"] = Json("\"pro\"") }));

        Assert.True(proj.TryResolveVariable("plan", out var resolved));
        Assert.Equal(proj.GetVariable<object>("plan"), resolved);
    }

    [Fact]
    public void Promoted_node_output_resolves_identically_on_both_paths()
    {
        // Promoted-variable pattern: "<nodeId>_<outputHandle>".
        var proj = new WorkflowExecutor.WorkflowStateProjection(
            Run(nodes: ("n1", new Dictionary<string, object> { ["out"] = Json("200") })));

        Assert.True(proj.TryResolveVariable("n1_out", out var resolved));
        Assert.Equal(proj.GetVariable<object>("n1_out"), resolved);
    }

    [Fact]
    public void A_present_but_null_global_is_found_on_both_paths()
    {
        // The crux of D7: a legitimately-null value must report as FOUND (not a miss). Both paths agree
        // it is present and both yield null.
        var run = Run(globals: new Dictionary<string, object>());
        run.GlobalVariables["maybe"] = null!; // present key, null value
        var proj = new WorkflowExecutor.WorkflowStateProjection(run);

        Assert.True(proj.TryResolveVariable("maybe", out var resolved));
        Assert.Null(resolved);
        Assert.Null(proj.GetVariable<object>("maybe"));
    }

    [Fact]
    public void Dotted_path_into_a_structured_global_resolves_on_both_paths()
    {
        // A dragged ref targeting a field of a structured global (e.g. the inbound `signal`) resolves
        // by walking the dotted path; both resolution paths must agree, and a missing leaf is a miss.
        var proj = new WorkflowExecutor.WorkflowStateProjection(
            Run(globals: new Dictionary<string, object>
            {
                ["signal"] = Json("""{ "type":"2", "active":true, "params": { "valueA":"x", "n":5 } }"""),
            }));

        Assert.True(proj.TryResolveVariable("signal.type", out var t));
        Assert.Equal(proj.GetVariable<object>("signal.type"), t);
        Assert.Equal("2", proj.GetVariable<string>("signal.type"));

        Assert.True(proj.TryResolveVariable("signal.params.valueA", out var v));
        Assert.Equal(proj.GetVariable<object>("signal.params.valueA"), v);
        Assert.Equal("x", proj.GetVariable<string>("signal.params.valueA"));

        Assert.False(proj.TryResolveVariable("signal.params.missing", out var miss));
        Assert.Null(miss);
    }

    [Fact]
    public void A_genuinely_missing_ref_is_a_miss_on_the_condition_path()
    {
        // The divergence the Condition task deliberately introduces: a missing ref is reported as NOT
        // found (→ RESOLUTION_FAILED) where the generic path can only return a value-shaped null. The
        // found-ness flag is exactly the extra bit the generic GetVariable cannot express.
        var proj = new WorkflowExecutor.WorkflowStateProjection(Run());

        Assert.False(proj.TryResolveVariable("absent", out var resolved));
        Assert.Null(resolved);
        Assert.Null(proj.GetVariable<object>("absent")); // generic path collapses the miss to null
    }
}
