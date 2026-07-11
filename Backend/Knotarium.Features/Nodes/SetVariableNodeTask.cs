using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.NodeRuntime;

namespace Knotarium.Features.Nodes;

public class SetVariableNodeTask : INodeTask
{
    public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var variableName = context.Inputs.TryGetValue("variableName", out var nameObj) ? nameObj?.ToString() : null;
        context.Inputs.TryGetValue("value", out var value);

        if (string.IsNullOrEmpty(variableName))
        {
            return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success());
        }

        try
        {
            VariableWriter.Write(context.Variables, variableName, value);
        }
        catch (VariableTreeException ex)
        {
            return Task.FromResult<LegacyNodeResult>(
                new LegacyNodeResult.Failure($"Set Variable '{variableName}': {ex.Message}"));
        }

        return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success());
    }
}
