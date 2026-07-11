export const DEFAULT_DECLARATIVE_MANIFEST = `id: custom.declarative.node
version: 1.0.0
displayName: Custom Declarative Node
category: Utility
tier: declarative
sideEffectKind: IdempotentSideEffect
recoveryMode: RetryAutomatically
defaultTimeoutSeconds: 30
capabilities:
  - logging
parameters:
  - name: inputMessage
    type: string
    required: true
    expression: true
outputs:
  - name: success
  - name: error
`;

export const DEFAULT_DECLARATIVE_TESTS = `- name: Test successful execution
  inputs:
    inputMessage: "Hello Knot Garden!"
  expectedOutput: success
  expectedPayload:
    message: "Received: Hello Knot Garden!"
`;

export const DEFAULT_COMPILED_MANIFEST = `id: custom.compiled.node
version: 1.0.0
displayName: Custom Compiled Node
category: Utility
tier: compiled
sideEffectKind: IdempotentSideEffect
recoveryMode: RetryAutomatically
defaultTimeoutSeconds: 30
capabilities:
  - logging
  - http
parameters:
  - name: targetUrl
    type: string
    required: true
    expression: true
outputs:
  - name: success
  - name: error
`;

export const DEFAULT_COMPILED_EXECUTOR = `using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;

namespace KnotGarden.Nodes
{
    public class CustomCompiledExecutor : INodeExecutor
    {
        public async ValueTask<NodeResult> ExecuteAsync(
            NodeInput input,
            INodeContext context,
            CancellationToken cancellationToken)
        {
            context.Logger.LogInformation("Custom Compiled Node starting execution...");
            
            if (!input.Parameters.TryGetValue("targetUrl", out var urlElement))
            {
                return new NodeResult("error", JsonSerializer.SerializeToElement(new { error = "targetUrl parameter missing" }), NodeExecutionStatus.Failed);
            }
            
            var url = urlElement.GetString();
            context.Logger.LogInformation($"Triggering HTTP request to: {url}");
            
            // Example of using the injected context capabilities:
            // if (context.Http != null) { ... }
            
            var payload = JsonSerializer.SerializeToElement(new {
                status = "success",
                url = url,
                timestamp = DateTimeOffset.UtcNow
            });
            
            return new NodeResult("success", payload, NodeExecutionStatus.Succeeded);
        }
    }
}
`;

export const DEFAULT_COMPILED_TESTS = `- name: Test with sample URL
  inputs:
    targetUrl: "https://api.github.com"
  expectedOutput: success
`;
