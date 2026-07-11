using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Nodes;
using Xunit;

namespace KnotGarden.Tests.Nodes;

public class EndNodeTaskTests
{
    [Fact]
    public async Task EndNodeTask_ReturnsEmptySuccess()
    {
        // Arrange
        var task = new EndNodeTask();
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("end-1"),
            Inputs: new Dictionary<string, object>(),
            GlobalVariables: new Dictionary<string, object>()
        );

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var successResult = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Null(successResult.Outputs);
    }

    [Fact]
    public async Task EndNodeTask_AppliesSubflowOutputs_CopyingVarsBackToCaller()
    {
        // Arrange: a subflow-internal variable that should surface under a caller-facing name.
        var task = new EndNodeTask();
        var globals = new Dictionary<string, object> { ["total"] = 99L };
        var inputs = new Dictionary<string, object>
        {
            ["__subflowOutputs"] = new List<object?>
            {
                new Dictionary<string, object> { ["source"] = "total", ["target"] = "orderTotal" },
            },
        };
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("subflow-x/end-1"),
            Inputs: inputs,
            GlobalVariables: globals
        );

        // Act
        await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal(99L, globals["orderTotal"]);
    }
}
