using System.Collections;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Nodes;

public class LogNodeTask : INodeTask
{
    private readonly ILogger<LogNodeTask> _logger;

    public LogNodeTask(ILogger<LogNodeTask> logger)
    {
        _logger = logger;
    }

    private static string Stringify(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        JsonElement el => el.ToString(),               // already has a useful ToString
        IEnumerable _ => JsonSerializer.Serialize(value), // List, array, etc. → JSON
        _ when value.GetType().IsPrimitive => value.ToString() ?? string.Empty,
        _ => JsonSerializer.Serialize(value)
    };

    public Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var message = context.Inputs.TryGetValue("message", out var msgObj) ? Stringify(msgObj) : string.Empty;
        if (string.IsNullOrEmpty(message))
        {
            message = "log message";
        }

        // Perform basic global variable substitution if variables exist
        if (context.GlobalVariables != null)
        {
            foreach (var kvp in context.GlobalVariables)
            {
                var placeholder = "{" + kvp.Key + "}";
                if (message.Contains(placeholder))
                {
                    message = message.Replace(placeholder, kvp.Value?.ToString() ?? string.Empty);
                }
            }
        }

        _logger.LogInformation("Workflow Log [{ExecutionId}] [Node:{NodeId}]: {Message}", 
            context.ExecutionId, context.NodeId, message);

        return Task.FromResult<LegacyNodeResult>(new LegacyNodeResult.Success(new Dictionary<string, object>
        {
            ["result"] = message   // single data port; wiring Log→next collects the value via the 'result' handle
        }));
    }
}
