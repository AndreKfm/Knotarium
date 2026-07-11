using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Execution;
using Xunit;

namespace KnotGarden.Tests.Execution;

public class ReplayPlanCalculatorTests
{
    private static NodeId N(string id) => NodeId.Create(id);

    private static ExecutionPlan PlanFrom(params (string From, string To)[] edges)
    {
        var nodeIds = edges
            .SelectMany(edge => new[] { edge.From, edge.To })
            .Distinct()
            .Select(N)
            .ToImmutableArray();

        var plannedNodes = nodeIds
            .Select(id => new PlannedNode(id, "noop", new Dictionary<string, object>()))
            .ToImmutableArray();

        var plannedEdges = edges
            .Select((edge, index) => new PlannedEdge($"e{index}", N(edge.From), "result", N(edge.To), "input"))
            .ToImmutableArray();

        var adjacency = plannedEdges
            .GroupBy(edge => edge.From)
            .ToImmutableDictionary(group => group.Key, group => group.Select(edge => edge.To).ToImmutableArray());

        return new ExecutionPlan(
            new WorkflowDefinitionId("wf"),
            1,
            plannedNodes,
            plannedEdges,
            adjacency,
            ImmutableArray<NodeId>.Empty);
    }

    private static NodeState Completed(string nodeId) => new()
    {
        NodeId = N(nodeId),
        Status = NodeStatus.Completed
    };

    [Fact]
    public void Linear_ResetsCutPointAndAllDownstream_SeedsUpstream()
    {
        // a -> b -> c -> d, cut at c
        var plan = PlanFrom(("a", "b"), ("b", "c"), ("c", "d"));
        var sources = new[] { Completed("a"), Completed("b"), Completed("c"), Completed("d") };

        var result = ReplayPlanCalculator.Compute(plan, N("c"), sources);

        Assert.Equal(new[] { "c", "d" }, result.ResetSet.Select(n => n.Value).OrderBy(v => v));
        Assert.Equal(new[] { "a", "b" }, result.SeedSet.Select(s => s.NodeId.Value).OrderBy(v => v));
    }

    [Fact]
    public void Branch_ResetsBothBranchesBelowCutPoint()
    {
        // a -> b ; b -> c ; b -> d, cut at b
        var plan = PlanFrom(("a", "b"), ("b", "c"), ("b", "d"));
        var sources = new[] { Completed("a"), Completed("b"), Completed("c"), Completed("d") };

        var result = ReplayPlanCalculator.Compute(plan, N("b"), sources);

        Assert.Equal(new[] { "b", "c", "d" }, result.ResetSet.Select(n => n.Value).OrderBy(v => v));
        Assert.Equal(new[] { "a" }, result.SeedSet.Select(s => s.NodeId.Value).OrderBy(v => v));
    }

    [Fact]
    public void Diamond_JoinNodeLandsInResetSetWhenReachableFromCutPoint()
    {
        // a -> b ; a -> c ; b -> d ; c -> d (join), cut at b
        var plan = PlanFrom(("a", "b"), ("a", "c"), ("b", "d"), ("c", "d"));
        var sources = new[] { Completed("a"), Completed("b"), Completed("c"), Completed("d") };

        var result = ReplayPlanCalculator.Compute(plan, N("b"), sources);

        // d is reachable from b, so the join node is reset even though c (its other parent) is seeded.
        Assert.Equal(new[] { "b", "d" }, result.ResetSet.Select(n => n.Value).OrderBy(v => v));
        Assert.Equal(new[] { "a", "c" }, result.SeedSet.Select(s => s.NodeId.Value).OrderBy(v => v));
    }

    [Fact]
    public void SelfLoop_DoesNotInfiniteLoop_AndResetsCutPoint()
    {
        // a -> b ; b -> b (self loop), cut at b
        var plan = PlanFrom(("a", "b"), ("b", "b"));
        var sources = new[] { Completed("a"), Completed("b") };

        var result = ReplayPlanCalculator.Compute(plan, N("b"), sources);

        Assert.Equal(new[] { "b" }, result.ResetSet.Select(n => n.Value).OrderBy(v => v));
        Assert.Equal(new[] { "a" }, result.SeedSet.Select(s => s.NodeId.Value).OrderBy(v => v));
    }

    [Fact]
    public void NonCompletedSourceStates_AreNotSeeded()
    {
        var plan = PlanFrom(("a", "b"), ("b", "c"));
        var sources = new[]
        {
            Completed("a"),
            new NodeState { NodeId = N("b"), Status = NodeStatus.Failed }
        };

        var result = ReplayPlanCalculator.Compute(plan, N("c"), sources);

        // b failed in the source run, so even though it's upstream of the cut point it is not seeded.
        Assert.Equal(new[] { "a" }, result.SeedSet.Select(s => s.NodeId.Value).OrderBy(v => v));
        Assert.Contains(N("c"), result.ResetSet);
    }
}
