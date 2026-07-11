using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Xunit;

namespace Knotarium.Tests.Nodes;

public class SetVariablesNodeTaskTests
{
    private static NodeExecutionContext Context(Dictionary<string, object> globals, params (string name, object? value)[] rows)
    {
        var list = new List<object>();
        foreach (var (name, value) in rows)
            list.Add(new Dictionary<string, object?> { ["name"] = name, ["value"] = value });
        return new(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("setvars-x"),
            Inputs: new Dictionary<string, object> { ["variables"] = list },
            GlobalVariables: globals);
    }

    private static JsonElement AsElement(object value)
        => value is JsonElement je ? je : JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task SetVariables_FlatRow_StoresRawValue()
    {
        var globals = new Dictionary<string, object>();
        var result = await new SetVariablesNodeTask().ExecuteAsync(
            Context(globals, ("single", 1L)), CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal(1, Convert.ToInt32(globals["single"]));
    }

    [Fact]
    public async Task SetVariables_KeyedRow_DeepSetsIntoHeadContainer()
    {
        var globals = new Dictionary<string, object>();
        var result = await new SetVariablesNodeTask().ExecuteAsync(
            Context(globals, ("multiple[\"value\"]", "aha")), CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.False(globals.ContainsKey("multiple[\"value\"]"));
        var dict = AsElement(globals["multiple"]);
        Assert.Equal(JsonValueKind.Object, dict.ValueKind);
        Assert.Equal("aha", dict.GetProperty("value").GetString());
    }

    [Fact]
    public async Task SetVariables_MultipleKeyedRowsSameHead_PreserveSiblings()
    {
        var globals = new Dictionary<string, object>();
        var result = await new SetVariablesNodeTask().ExecuteAsync(
            Context(globals, ("cfg[\"a\"]", 1L), ("cfg[\"b\"]", 2L)), CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Success>(result);
        var dict = AsElement(globals["cfg"]);
        Assert.Equal(1, dict.GetProperty("a").GetInt32());
        Assert.Equal(2, dict.GetProperty("b").GetInt32());
    }

    [Fact]
    public async Task SetVariables_TypeConflict_FailsTheNode()
    {
        var globals = new Dictionary<string, object> { ["x"] = 5 };
        var result = await new SetVariablesNodeTask().ExecuteAsync(
            Context(globals, ("x[\"a\"]", 1L)), CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Failure>(result);
    }
}
