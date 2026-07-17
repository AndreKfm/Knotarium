// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Knotarium.Core.Domain;
using Xunit;

namespace Knotarium.Tests.NodeE2E;

/// <summary>
/// Real-node-through-real-engine e2e for the pure / self-contained built-in nodes (no external I/O). Each
/// test drives the shipped node task via <see cref="NodeE2EHarness"/> and asserts the persisted output the
/// engine recorded, proving the node functions end-to-end when the executor runs it.
/// </summary>
[Collection(WorkflowExecutionIsolationCollection.Name)]
public class CoreNodeE2ETests
{
    [Fact]
    public async Task Start_passes_configured_properties_through_to_outputs()
    {
        using var harness = new NodeE2EHarness();

        // Start is the entry node; the executor merges its Properties into its inputs, and StartNodeTask
        // passes inputs through as outputs. Drive it as a bare start -> end graph.
        var start = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object> { ["seed"] = "go" });
        var end = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());
        var edges = new[] { new EdgeDefinition("e", start.Id, "result", end.Id, "in") };

        var run = await harness.RunWorkflowAsync(new[] { start, end }, edges);

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal(NodeStatus.Completed, run.State("start-1").Status);
        Assert.Equal("go", run.State("start-1").Outputs["seed"].ToString());
    }

    [Fact]
    public async Task Log_completes_and_emits_message_on_result()
    {
        using var harness = new NodeE2EHarness();

        var run = await harness.RunNodeAsync("log", new Dictionary<string, object> { ["message"] = "hello e2e" });

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal(NodeStatus.Completed, run.Node.Status);
        Assert.Equal("hello e2e", run.Node.Outputs["result"].ToString());
        Assert.Equal(NodeStatus.Completed, run.State("end-1").Status);
    }

    [Fact]
    public async Task Log_substitutes_global_variables_into_the_message()
    {
        using var harness = new NodeE2EHarness();

        // setVariable writes {who}=world, then Log interpolates it.
        var start = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var setVar = new NodeDefinition(NodeId.Create("set-1"), "setVariable",
            new Dictionary<string, object> { ["variableName"] = "who", ["value"] = "world" });
        var log = new NodeDefinition(NodeId.Create("log-1"), "log",
            new Dictionary<string, object> { ["message"] = "hi {who}" });
        var edges = new[]
        {
            new EdgeDefinition("e1", start.Id, "result", setVar.Id, "in"),
            new EdgeDefinition("e2", setVar.Id, "result", log.Id, "in"),
        };

        var run = await harness.RunWorkflowAsync(new[] { start, setVar, log }, edges);

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal("hi world", run.State("log-1").Outputs["result"].ToString());
    }

    [Fact]
    public async Task SetVariable_writes_a_global_readable_after_the_run()
    {
        using var harness = new NodeE2EHarness();

        var run = await harness.RunNodeAsync("setVariable",
            new Dictionary<string, object> { ["variableName"] = "answer", ["value"] = 42 });

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal(NodeStatus.Completed, run.Node.Status);
        Assert.True(run.GlobalVariables.ContainsKey("answer"));
        Assert.Equal("42", run.GlobalVariables["answer"].ToString());
    }

    [Fact]
    public async Task SetVariables_bulk_sets_globals_and_reports_count()
    {
        using var harness = new NodeE2EHarness();

        var rows = new List<Dictionary<string, object>>
        {
            new() { ["name"] = "a", ["value"] = 1 },
            new() { ["name"] = "b", ["value"] = 2 },
        };

        var run = await harness.RunNodeAsync("setVariables",
            new Dictionary<string, object> { ["variables"] = rows });

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal("2", run.Node.Outputs["count"].ToString());
        Assert.Equal("1", run.GlobalVariables["a"].ToString());
        Assert.Equal("2", run.GlobalVariables["b"].ToString());
    }

    [Fact]
    public async Task Join_emits_its_aggregated_results_on_both_ports()
    {
        using var harness = new NodeE2EHarness();

        // The executor normally aggregates upstream fan-in into "results"; here we seed it via config to
        // exercise the node's own contract (result + results carry the array).
        var results = new List<object> { "x", "y", "z" };
        var run = await harness.RunNodeAsync("join",
            new Dictionary<string, object> { ["results"] = results });

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal(NodeStatus.Completed, run.Node.Status);
        Assert.True(run.Node.Outputs.ContainsKey("result"));
        Assert.True(run.Node.Outputs.ContainsKey("results"));
    }

    [Fact]
    public async Task Delay_below_one_second_completes_inline()
    {
        using var harness = new NodeE2EHarness();

        var run = await harness.RunNodeAsync("delay", new Dictionary<string, object> { ["delayMs"] = 10 });

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal(NodeStatus.Completed, run.Node.Status);
    }

    [Fact]
    public async Task Delay_of_one_second_or_more_suspends_the_run()
    {
        using var harness = new NodeE2EHarness();

        // A Delay >= 1s returns the Delay result variant, which the engine turns into a suspended run
        // (resumed later by a scheduled work item). Assert the suspend, not a completion.
        var run = await harness.RunNodeAsync("delay", new Dictionary<string, object> { ["delayMs"] = 1000 });

        Assert.Equal(ExecutionStatus.Suspended, run.Status);
        Assert.Equal(NodeStatus.Waiting, run.Node.Status);
    }

    [Theory]
    [InlineData(5, "left")]
    [InlineData(-3, "right")]
    public async Task Condition_routes_to_the_branch_matching_its_logic(int value, string expectedBranch)
    {
        using var harness = new NodeE2EHarness();

        // Legacy flat condition: value > 0 ? true-branch : false-branch.
        var start = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var cond = new NodeDefinition(NodeId.Create("cond-1"), "condition", new Dictionary<string, object>
        {
            ["left"] = value,
            ["operator"] = "greaterThan",
            ["right"] = 0,
        });
        var left = new NodeDefinition(NodeId.Create("left"), "log", new Dictionary<string, object> { ["message"] = "L" });
        var right = new NodeDefinition(NodeId.Create("right"), "log", new Dictionary<string, object> { ["message"] = "R" });
        var edges = new[]
        {
            new EdgeDefinition("e0", start.Id, "result", cond.Id, "in"),
            new EdgeDefinition("eT", cond.Id, "true", left.Id, "in"),
            new EdgeDefinition("eF", cond.Id, "false", right.Id, "in"),
        };

        var run = await harness.RunWorkflowAsync(new[] { start, cond, left, right }, edges);

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal(NodeStatus.Completed, run.State("cond-1").Status);
        Assert.True(run.Ran(expectedBranch));
        Assert.False(run.Ran(expectedBranch == "left" ? "right" : "left"));
    }

    [Fact]
    public async Task ForLoop_foreach_first_pass_routes_to_start_with_the_first_item()
    {
        using var harness = new NodeE2EHarness();

        // start -> forLoop; forLoop "start" -> body. Assert the loop opens the body branch for item[0].
        var start = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var loop = new NodeDefinition(NodeId.Create("loop-1"), "forLoop", new Dictionary<string, object>
        {
            ["mode"] = "foreach",
            ["collection"] = new List<object> { "a", "b", "c" },
        });
        var body = new NodeDefinition(NodeId.Create("body-1"), "log", new Dictionary<string, object> { ["message"] = "iter" });
        var edges = new[]
        {
            new EdgeDefinition("e0", start.Id, "result", loop.Id, "in"),
            new EdgeDefinition("eBody", loop.Id, "start", body.Id, "in"),
        };

        var run = await harness.RunWorkflowAsync(new[] { start, loop, body }, edges);

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        var loopState = run.State("loop-1");
        Assert.Equal(NodeStatus.Completed, loopState.Status);
        Assert.Equal("start", loopState.Outputs["selectedPort"].ToString());
        Assert.Equal("0", loopState.Outputs["index"].ToString());
        Assert.Equal("a", loopState.Outputs["item"].ToString());
        Assert.True(run.Ran("body-1"));
    }

    [Fact]
    public async Task ForLoop_over_empty_collection_routes_straight_to_success()
    {
        using var harness = new NodeE2EHarness();

        var start = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var loop = new NodeDefinition(NodeId.Create("loop-1"), "forLoop", new Dictionary<string, object>
        {
            ["mode"] = "foreach",
            ["collection"] = new List<object>(),
        });
        var body = new NodeDefinition(NodeId.Create("body-1"), "log", new Dictionary<string, object> { ["message"] = "iter" });
        var end = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());
        var edges = new[]
        {
            new EdgeDefinition("e0", start.Id, "result", loop.Id, "in"),
            new EdgeDefinition("eBody", loop.Id, "start", body.Id, "in"),
            new EdgeDefinition("eDone", loop.Id, "success", end.Id, "in"),
        };

        var run = await harness.RunWorkflowAsync(new[] { start, loop, body, end }, edges);

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal("success", run.State("loop-1").Outputs["selectedPort"].ToString());
        Assert.True(run.Ran("end-1"));
        Assert.False(run.Ran("body-1"));
    }
}
