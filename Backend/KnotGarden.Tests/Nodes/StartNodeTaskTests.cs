using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Nodes;
using Xunit;

namespace KnotGarden.Tests.Nodes;

public class StartNodeTaskTests
{
    [Fact]
    public async Task StartNodeTask_PropagatesInputsToOutputs()
    {
        // Arrange
        var task = new StartNodeTask();
        var inputs = new Dictionary<string, object>
        {
            ["inputVal"] = "hello",
            ["numberVal"] = 123.45
        };
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("start-1"),
            Inputs: inputs,
            GlobalVariables: new Dictionary<string, object>()
        );

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var successResult = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.NotNull(successResult.Outputs);
        Assert.Equal("hello", successResult.Outputs["inputVal"]);
        Assert.Equal(123.45, successResult.Outputs["numberVal"]);
    }

    [Fact]
    public async Task StartNodeTask_AppliesSubflowInputs_AndHidesInternalKeyFromOutputs()
    {
        // Arrange: simulate the inlined subflow start node carrying an (already-evaluated) input map.
        var task = new StartNodeTask();
        var globals = new Dictionary<string, object>();
        var inputs = new Dictionary<string, object>
        {
            ["__subflowInputs"] = new List<object?>
            {
                new Dictionary<string, object> { ["target"] = "id", ["value"] = 42L },
                new Dictionary<string, object> { ["target"] = "label", ["value"] = "hi" },
            },
        };
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("subflow-x/start-1"),
            Inputs: inputs,
            GlobalVariables: globals
        );

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert: the subflow's input variables were set in the shared scope...
        Assert.Equal(42L, globals["id"]);
        Assert.Equal("hi", globals["label"]);
        // ...and the internal binding key is not leaked as a node output.
        var successResult = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.NotNull(successResult.Outputs);
        Assert.False(successResult.Outputs!.ContainsKey("__subflowInputs"));
    }
}
