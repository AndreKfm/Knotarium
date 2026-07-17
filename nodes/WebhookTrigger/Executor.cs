// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Nodes;

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
