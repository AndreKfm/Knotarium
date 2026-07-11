using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Nodes;
using Xunit;

namespace KnotGarden.Tests.Nodes;

public class ConditionNodeTaskTests
{
    [Theory]
    [InlineData(5.0, 5.0, "Equal", "true")]
    [InlineData(5.0, 10.0, "Equal", "false")]
    [InlineData(5.0, 10.0, "NotEqual", "true")]
    [InlineData(10.0, 5.0, "GreaterThan", "true")]
    [InlineData(5.0, 10.0, "LessThan", "true")]
    [InlineData(5.0, 5.0, "GreaterThanOrEqual", "true")]
    [InlineData(5.0, 5.0, "LessThanOrEqual", "true")]
    public async Task Evaluate_NumericComparisons(double left, double right, string op, string expectedPort)
    {
        // Arrange
        var task = new ConditionNodeTask();
        var inputs = new Dictionary<string, object>
        {
            ["left"] = left,
            ["right"] = right,
            ["operator"] = op
        };
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("cond-1"),
            Inputs: inputs,
            GlobalVariables: new Dictionary<string, object>()
        );

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.NotNull(success.Outputs);
        Assert.Equal(expectedPort, success.Outputs["selectedPort"]);
    }

    [Theory]
    [InlineData("apple", "apple", "Equal", "true")]
    [InlineData("apple", "orange", "Equal", "false")]
    [InlineData("apple", "orange", "NotEqual", "true")]
    [InlineData("pineapple", "apple", "Contains", "true")]
    [InlineData("apple", "pineapple", "Contains", "false")]
    public async Task Evaluate_StringComparisons(string left, string right, string op, string expectedPort)
    {
        // Arrange
        var task = new ConditionNodeTask();
        var inputs = new Dictionary<string, object>
        {
            ["left"] = left,
            ["right"] = right,
            ["operator"] = op
        };
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("cond-1"),
            Inputs: inputs,
            GlobalVariables: new Dictionary<string, object>()
        );

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.NotNull(success.Outputs);
        Assert.Equal(expectedPort, success.Outputs["selectedPort"]);
    }

    [Theory]
    [InlineData(true, true, "Equal", "true")]
    [InlineData(true, false, "Equal", "false")]
    [InlineData(true, false, "NotEqual", "true")]
    public async Task Evaluate_BooleanComparisons(bool left, bool right, string op, string expectedPort)
    {
        // Arrange
        var task = new ConditionNodeTask();
        var inputs = new Dictionary<string, object>
        {
            ["left"] = left,
            ["right"] = right,
            ["operator"] = op
        };
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("cond-1"),
            Inputs: inputs,
            GlobalVariables: new Dictionary<string, object>()
        );

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.NotNull(success.Outputs);
        Assert.Equal(expectedPort, success.Outputs["selectedPort"]);
    }

    [Fact]
    public async Task Evaluate_StrictTypeMismatch_EvaluatesToFalse()
    {
        // Arrange
        var task = new ConditionNodeTask();
        var inputs = new Dictionary<string, object>
        {
            ["left"] = "5", // String
            ["right"] = 5.0, // Double
            ["operator"] = "Equal"
        };
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("cond-1"),
            Inputs: inputs,
            GlobalVariables: new Dictionary<string, object>()
        );

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.NotNull(success.Outputs);
        Assert.Equal("false", success.Outputs["selectedPort"]); // Different types return false!
    }

    [Fact]
    public async Task Evaluate_UnboxesJsonElementCorrectly()
    {
        // Arrange
        using var doc = JsonDocument.Parse("{\"num\": 15.5, \"str\": \"test\", \"b\": true}");
        var leftElem = doc.RootElement.GetProperty("num");

        var task = new ConditionNodeTask();
        var inputs = new Dictionary<string, object>
        {
            ["left"] = leftElem,
            ["right"] = 15.5,
            ["operator"] = "Equal"
        };
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("cond-1"),
            Inputs: inputs,
            GlobalVariables: new Dictionary<string, object>()
        );

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.NotNull(success.Outputs);
        Assert.Equal("true", success.Outputs["selectedPort"]);
    }
}
