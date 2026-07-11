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

public class SetVariableNodeTaskTests
{
    private static NodeExecutionContext Context(Dictionary<string, object> globals, string variableName, object? value)
        => new(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("set-x"),
            Inputs: new Dictionary<string, object> { ["variableName"] = variableName, ["value"] = value! },
            GlobalVariables: globals);

    private static JsonElement AsElement(object value)
        => value is JsonElement je ? je : JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task SetVariable_AddsToGlobalVariablesInPlace()
    {
        // Arrange
        var task = new SetVariableNodeTask();
        var globalVars = new Dictionary<string, object>
        {
            ["existingKey"] = "oldValue"
        };
        var inputs = new Dictionary<string, object>
        {
            ["variableName"] = "newKey",
            ["value"] = "newValue"
        };
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("set-1"),
            Inputs: inputs,
            GlobalVariables: globalVars
        );

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.True(globalVars.ContainsKey("newKey"));
        Assert.Equal("newValue", globalVars["newKey"]);
        Assert.Equal("oldValue", globalVars["existingKey"]);
    }

    [Fact]
    public async Task SetVariable_ClearsValueWhenNull()
    {
        // Arrange
        var task = new SetVariableNodeTask();
        var globalVars = new Dictionary<string, object>
        {
            ["tempKey"] = "tempValue"
        };
        var inputs = new Dictionary<string, object>
        {
            ["variableName"] = "tempKey",
            ["value"] = null!
        };
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("set-2"),
            Inputs: inputs,
            GlobalVariables: globalVars
        );

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.False(globalVars.ContainsKey("tempKey"));
    }

    [Fact]
    public async Task SetVariable_KeyedPathOnAbsentVar_AutoCreatesObject()
    {
        var globals = new Dictionary<string, object>();
        var result = await new SetVariableNodeTask().ExecuteAsync(
            Context(globals, "myDict[\"name\"]", 1), CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Success>(result);
        var dict = AsElement(globals["myDict"]);
        Assert.Equal(JsonValueKind.Object, dict.ValueKind);
        Assert.Equal(1, dict.GetProperty("name").GetInt32());
    }

    [Fact]
    public async Task SetVariable_IndexPathOnAbsentVar_AutoCreatesArray()
    {
        var globals = new Dictionary<string, object>();
        var result = await new SetVariableNodeTask().ExecuteAsync(
            Context(globals, "list[0]", "x"), CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Success>(result);
        var arr = AsElement(globals["list"]);
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal("x", arr[0].GetString());
    }

    [Fact]
    public async Task SetVariable_KeyedPath_PreservesSiblingKeys()
    {
        var globals = new Dictionary<string, object>
        {
            ["myDict"] = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["a"] = 1 })
        };
        var result = await new SetVariableNodeTask().ExecuteAsync(
            Context(globals, "myDict[\"b\"]", 2), CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Success>(result);
        var dict = AsElement(globals["myDict"]);
        Assert.Equal(1, dict.GetProperty("a").GetInt32());
        Assert.Equal(2, dict.GetProperty("b").GetInt32());
    }

    [Fact]
    public async Task SetVariable_ArrayAppendAtLength_Appends()
    {
        var globals = new Dictionary<string, object>
        {
            ["list"] = JsonSerializer.SerializeToElement(new[] { 10 })
        };
        var result = await new SetVariableNodeTask().ExecuteAsync(
            Context(globals, "list[1]", 20), CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Success>(result);
        var arr = AsElement(globals["list"]);
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal(20, arr[1].GetInt32());
    }

    [Fact]
    public async Task SetVariable_TypeConflictWithScalar_Fails()
    {
        var globals = new Dictionary<string, object> { ["myVar"] = 5 };
        var result = await new SetVariableNodeTask().ExecuteAsync(
            Context(globals, "myVar[\"a\"]", 1), CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Failure>(result);
    }

    [Fact]
    public async Task SetVariable_IndexIntoObject_Fails()
    {
        var globals = new Dictionary<string, object>
        {
            ["obj"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>())
        };
        var result = await new SetVariableNodeTask().ExecuteAsync(
            Context(globals, "obj[0]", 1), CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Failure>(result);
    }

    [Fact]
    public async Task SetVariable_FlatName_RemainsUnchangedBehavior()
    {
        var globals = new Dictionary<string, object>();
        var result = await new SetVariableNodeTask().ExecuteAsync(
            Context(globals, "plain", "v"), CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Success>(result);
        // Flat write stores the raw value as-is (not wrapped in a JsonElement tree).
        Assert.Equal("v", globals["plain"]);
    }
}
