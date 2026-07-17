// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Execution;

/// <summary>
/// Pure helper that, given an execution plan, a cut-point node and the source run's persisted
/// node states, computes which nodes must be re-executed (the <em>reset set</em>) and which
/// completed source states should be inherited as historical inputs (the <em>seed set</em>).
/// </summary>
public static class ReplayPlanCalculator
{
    /// <param name="plan">The compiled plan the replay runs against.</param>
    /// <param name="fromNodeId">The cut-point node the replay starts from.</param>
    /// <param name="sourceNodeStates">The persisted node states of the source run.</param>
    public static ReplayPlan Compute(
        ExecutionPlan plan,
        NodeId fromNodeId,
        IReadOnlyCollection<NodeState> sourceNodeStates)
    {
        // Reset set = the cut-point node plus its transitive forward closure. Forward-BFS over
        // plan.Edges (same shape as WorkflowExecutor.ResetLoopBodyNodes). Every node reachable
        // from the cut point is re-executed for real; everything else is seeded from history.
        var resetSet = new HashSet<NodeId> { fromNodeId };
        var queue = new Queue<NodeId>();
        queue.Enqueue(fromNodeId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in plan.Edges.Where(edge => edge.From == current))
            {
                if (resetSet.Add(edge.To))
                {
                    queue.Enqueue(edge.To);
                }
            }
        }

        // Seed set = every completed source node state whose node is NOT in the reset set.
        // These carry the historical inputs/outputs the executor resolves predecessors from.
        var seedSet = sourceNodeStates
            .Where(state => state.Status == NodeStatus.Completed && !resetSet.Contains(state.NodeId))
            .ToList();

        return new ReplayPlan(resetSet, seedSet);
    }
}

/// <summary>
/// The result of <see cref="ReplayPlanCalculator.Compute"/>: the nodes to re-execute and the
/// completed source node states to inherit.
/// </summary>
public sealed record ReplayPlan(
    IReadOnlySet<NodeId> ResetSet,
    IReadOnlyList<NodeState> SeedSet);
