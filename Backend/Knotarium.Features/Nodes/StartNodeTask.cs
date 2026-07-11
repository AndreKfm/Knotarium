using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Nodes;

public class StartNodeTask : INodeTask
{
    public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        // When this start node belongs to an inlined subflow, apply its declared input map:
        // each entry sets a subflow variable from a value already evaluated (against the caller's
        // scope) by the executor. See WorkflowCompiler subflow inlining.
        if (context.Inputs.TryGetValue("__subflowInputs", out var inputsObj))
        {
            foreach (var (name, value) in SubflowMapping.ReadEntries(inputsObj, "target", "value"))
            {
                if (!string.IsNullOrEmpty(name))
                {
                    context.Variables.Set(name, value);
                }
            }
        }

        // Pass real inputs through as outputs (excluding the internal subflow-binding key).
        var outputs = new Dictionary<string, object>();
        if (context.Inputs != null)
        {
            foreach (var kvp in context.Inputs)
            {
                // Internal binding key and the editor-only interface declaration are not real outputs.
                if (kvp.Key == "__subflowInputs" || kvp.Key == "interfaceInputs")
                {
                    continue;
                }
                outputs[kvp.Key] = kvp.Value;
            }
        }

        return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(outputs));
    }
}
