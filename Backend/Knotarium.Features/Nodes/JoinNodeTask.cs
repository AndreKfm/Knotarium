using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Fan-in / Join node. It collects the outputs of all upstream branches that converge on it and
/// emits them as a single <c>results</c> array (preserving incoming-edge order).
/// <para>
/// The "wait-for-all" gate lives in <see cref="Execution.WorkflowExecutor"/>: a join node is not
/// executed until every one of its incoming-edge predecessors has completed. By the time this task
/// runs, the executor has already aggregated the branch values into the <c>results</c> input, so the
/// task is a thin pass-through that exposes them downstream.
/// </para>
/// <para>
/// Use a join after a fan-out where all branches are guaranteed to run (e.g. a Split into N branches,
/// or the branches downstream of a <c>parallelForEach</c>). Do not place it after a Condition/Switch
/// whose branches are mutually exclusive — the un-taken branch would never complete and the join
/// would wait forever.
/// </para>
/// </summary>
public class JoinNodeTask : INodeTask
{
    public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        // The executor pre-aggregates every completed branch output into "results".
        object results = context.Inputs.TryGetValue("results", out var aggregated) && aggregated != null
            ? aggregated
            : new List<object?>();

        var outputs = new Dictionary<string, object>
        {
            ["results"] = results,
            // "result" mirrors "results" so the generic single-value output port keeps working for
            // downstream nodes that just want "the joined value".
            ["result"] = results
        };

        return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(outputs));
    }
}
