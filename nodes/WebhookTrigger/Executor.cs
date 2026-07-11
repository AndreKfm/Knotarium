using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;

namespace KnotGarden.Nodes;

public class WebhookTriggerExecutor : INodeExecutor
{
    public ValueTask<NodeResult> ExecuteAsync(
        NodeInput input,
        INodeContext context,
        CancellationToken cancellationToken)
    {
        input.Parameters.TryGetValue("payload", out var payloadElem);
        return new ValueTask<NodeResult>(new NodeResult("result", payloadElem, NodeExecutionStatus.Succeeded));
    }
}
