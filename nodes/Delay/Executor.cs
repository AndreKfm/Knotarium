using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Nodes;

public class DelayExecutor : INodeExecutor
{
    public async ValueTask<NodeResult> ExecuteAsync(
        NodeInput input,
        INodeContext context,
        CancellationToken cancellationToken)
    {
        int durationMs = 0;

        if (input.Parameters.TryGetValue("delayMs", out var msElem))
        {
            if (msElem.ValueKind == JsonValueKind.Number)
            {
                durationMs = (int)msElem.GetDouble();
            }
            else if (int.TryParse(msElem.GetString(), out var parsedMs))
            {
                durationMs = parsedMs;
            }
        }
        else if (input.Parameters.TryGetValue("duration", out var durElem))
        {
            var durStr = durElem.GetString();
            if (TimeSpan.TryParse(durStr, System.Globalization.CultureInfo.InvariantCulture, out var ts))
            {
                durationMs = (int)ts.TotalMilliseconds;
            }
        }

        if (durationMs > 0)
        {
            try
            {
                await Task.Delay(durationMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new NodeResult("error", 
                    JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["error"] = "Delay execution was cancelled." }), 
                    NodeExecutionStatus.Cancelled);
            }
        }

        return new NodeResult("result", null, NodeExecutionStatus.Succeeded);
    }
}
