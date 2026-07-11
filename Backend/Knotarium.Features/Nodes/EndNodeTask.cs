using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Nodes;

public class EndNodeTask : INodeTask
{
    public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        // When this end node belongs to an inlined subflow, apply its declared output map: copy each
        // subflow variable back out into the caller's scope under the requested name.
        if (context.Inputs.TryGetValue("__subflowOutputs", out var outputsObj))
        {
            foreach (var (source, target) in SubflowMapping.ReadPairs(outputsObj, "source", "target"))
            {
                if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(target))
                {
                    context.Variables.Set(target, context.Variables.Get<object>(source));
                }
            }
        }

        return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success());
    }
}
