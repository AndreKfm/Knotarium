using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Nodes;

public class DelayNodeTask : INodeTask
{
    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var durationMs = 0;

        if (context.Inputs.TryGetValue("delayMs", out var msObj) && msObj != null)
        {
            if (msObj is double dVal)
            {
                durationMs = (int)dVal;
            }
            else if (int.TryParse(msObj.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                durationMs = parsed;
            }
        }
        else if (context.Inputs.TryGetValue("duration", out var durObj) && durObj != null)
        {
            if (TimeSpan.TryParse(durObj.ToString(), CultureInfo.InvariantCulture, out var ts))
            {
                durationMs = (int)ts.TotalMilliseconds;
            }
        }

        // Non-trivial waits SUSPEND the run (the engine schedules a timed resume and frees the worker) so a
        // long delay in one signal-triggered workflow no longer stalls others. Sub-second waits block inline
        // — cheaper than a suspend/resume DB round-trip, and they don't meaningfully accumulate.
        const int SuspendThresholdMs = 1000;
        if (durationMs >= SuspendThresholdMs)
        {
            return new LegacyNodeResult.Delay(durationMs);
        }

        if (durationMs > 0)
        {
            try
            {
                await Task.Delay(durationMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new LegacyNodeResult.Failure("Delay execution was cancelled.");
            }
        }

        return new LegacyNodeResult.Success();
    }
}
