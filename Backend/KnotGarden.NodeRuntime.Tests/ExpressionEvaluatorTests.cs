using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.NodeRuntime;

namespace KnotGarden.NodeRuntime.Tests;

public class ExpressionEvaluatorTests
{
    private class FakeWorkflowState : IWorkflowState
    {
        private readonly Dictionary<string, object?> _variables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(NodeId, string), JsonElement> _outputs = new();

        public T? GetVariable<T>(string name)
        {
            if (_variables.TryGetValue(name, out var val))
            {
                return (T?)val;
            }
            return default;
        }

        public void SetVariable(string name, object? value)
        {
            _variables[name] = value;
        }

        public void SetNodeOutput(NodeId nodeId, string outputName, object val)
        {
            var json = JsonSerializer.Serialize(val);
            var je = JsonSerializer.Deserialize<JsonElement>(json);
            _outputs[(nodeId, outputName)] = je;
        }

        public JsonElement? GetNodeOutput(NodeId nodeId, string outputName)
        {
            if (_outputs.TryGetValue((nodeId, outputName), out var je))
            {
                return je;
            }
            return null;
        }
    }

    [Fact]
    public void Evaluate_ReturnsLiteralText_WhenNoPlaceholders()
    {
        var state = new FakeWorkflowState();
        var result = ExpressionEvaluator.Evaluate("Hello World!", state);
        Assert.Equal("Hello World!", result);
    }

    [Fact]
    public void Evaluate_ResolvesLiteralExpressions()
    {
        var state = new FakeWorkflowState();
        
        Assert.Equal("Hello World", ExpressionEvaluator.Evaluate("Hello {{ 'World' }}", state));
        Assert.Equal(42, ExpressionEvaluator.Evaluate("{{ 42 }}", state));
        Assert.Equal(true, ExpressionEvaluator.Evaluate("{{ true }}", state));
        Assert.Equal(false, ExpressionEvaluator.Evaluate("{{ false }}", state));
        Assert.Null(ExpressionEvaluator.Evaluate("{{ null }}", state));
    }

    [Fact]
    public void Evaluate_ResolvesMathExpressions()
    {
        var state = new FakeWorkflowState();
        
        Assert.Equal(7.0, ExpressionEvaluator.Evaluate("{{ 5 + 2 }}", state));
        Assert.Equal(3.0, ExpressionEvaluator.Evaluate("{{ 5 - 2 }}", state));
        Assert.Equal(10.0, ExpressionEvaluator.Evaluate("{{ 5 * 2 }}", state));
        Assert.Equal(2.5, ExpressionEvaluator.Evaluate("{{ 5 / 2 }}", state));
        Assert.Equal(11.0, ExpressionEvaluator.Evaluate("{{ 5 + 3 * 2 }}", state)); // Precedence: 5 + (3 * 2)
        Assert.Equal(16.0, ExpressionEvaluator.Evaluate("{{ (5 + 3) * 2 }}", state)); // Parens: 8 * 2
    }

    [Fact]
    public void Evaluate_ResolvesComparisonExpressions()
    {
        var state = new FakeWorkflowState();

        Assert.Equal(true, ExpressionEvaluator.Evaluate("{{ 5 == 5 }}", state));
        Assert.Equal(false, ExpressionEvaluator.Evaluate("{{ 5 != 5 }}", state));
        Assert.Equal(true, ExpressionEvaluator.Evaluate("{{ 'abc' == 'abc' }}", state));
        Assert.Equal(false, ExpressionEvaluator.Evaluate("{{ 'abc' != 'abc' }}", state));
        Assert.Equal(true, ExpressionEvaluator.Evaluate("{{ true == true }}", state));
        Assert.Equal(false, ExpressionEvaluator.Evaluate("{{ true != true }}", state));
    }

    [Fact]
    public void Evaluate_ResolvesLogicalExpressions()
    {
        var state = new FakeWorkflowState();

        Assert.Equal(true, ExpressionEvaluator.Evaluate("{{ true && true }}", state));
        Assert.Equal(false, ExpressionEvaluator.Evaluate("{{ true && false }}", state));
        Assert.Equal(true, ExpressionEvaluator.Evaluate("{{ false || true }}", state));
        Assert.Equal(false, ExpressionEvaluator.Evaluate("{{ false || false }}", state));
        Assert.Equal(true, ExpressionEvaluator.Evaluate("{{ true && (false || true) }}", state));
    }

    [Fact]
    public void Evaluate_ResolvesVariables()
    {
        var state = new FakeWorkflowState();
        state.SetVariable("myVar", "variableValue");
        state.SetVariable("numberVar", 100);

        Assert.Equal("Value: variableValue", ExpressionEvaluator.Evaluate("Value: {{ $variables.myVar }}", state));
        Assert.Equal(100, ExpressionEvaluator.Evaluate("{{ $variables.numberVar }}", state));
    }

    [Fact]
    public void Evaluate_ResolvesNodeOutputs()
    {
        var state = new FakeWorkflowState();
        state.SetNodeOutput(NodeId.Create("start-1"), "success", "startValue");
        
        var nestedObject = new Dictionary<string, object>
        {
            ["user"] = new Dictionary<string, object>
            {
                ["name"] = "Alice",
                ["age"] = 30,
                ["roles"] = new List<string> { "admin", "user" }
            }
        };
        state.SetNodeOutput(NodeId.Create("fetch-user"), "payload", nestedObject);

        Assert.Equal("startValue", ExpressionEvaluator.Evaluate("{{ $node.start-1.output.success }}", state));
        Assert.Equal("Alice", ExpressionEvaluator.Evaluate("{{ $node.fetch-user.output.payload.user.name }}", state));
        Assert.Equal(30.0, ExpressionEvaluator.Evaluate("{{ $node.fetch-user.output.payload.user.age }}", state));
        Assert.Equal("admin", ExpressionEvaluator.Evaluate("{{ $node.fetch-user.output.payload.user.roles[0] }}", state));
        Assert.Equal("user", ExpressionEvaluator.Evaluate("{{ $node.fetch-user.output.payload.user.roles[1] }}", state));
    }

    [Fact]
    public void Evaluate_NavigatesIntoVariable_ByDottedMember()
    {
        var state = new FakeWorkflowState();
        state.SetVariable("d", JsonSerializer.Deserialize<JsonElement>("{\"name\":\"Alice\"}"));
        Assert.Equal("Alice", ExpressionEvaluator.Evaluate("{{ $variables.d.name }}", state));
    }

    [Fact]
    public void Evaluate_NavigatesIntoVariable_ByBracketKey()
    {
        var state = new FakeWorkflowState();
        state.SetVariable("d", JsonSerializer.Deserialize<JsonElement>("{\"name\":\"Alice\"}"));
        Assert.Equal("Alice", ExpressionEvaluator.Evaluate("{{ $variables.d[\"name\"] }}", state));
    }

    [Fact]
    public void Evaluate_NavigatesIntoVariable_ByIndex()
    {
        var state = new FakeWorkflowState();
        state.SetVariable("list", JsonSerializer.Deserialize<JsonElement>("[10,20,30]"));
        Assert.Equal(10.0, ExpressionEvaluator.Evaluate("{{ $variables.list[0] }}", state));
    }

    [Fact]
    public void Evaluate_NavigatesIntoVariable_DeepMixedPath()
    {
        var state = new FakeWorkflowState();
        state.SetVariable("cfg", JsonSerializer.Deserialize<JsonElement>(
            "{\"servers\":[{\"host\":\"h1\"},{\"host\":\"h2\"}]}"));
        Assert.Equal("h2", ExpressionEvaluator.Evaluate("{{ $variables.cfg.servers[1].host }}", state));
    }

    [Fact]
    public void Evaluate_VariableMemberMiss_ReturnsNull()
    {
        var state = new FakeWorkflowState();
        state.SetVariable("d", JsonSerializer.Deserialize<JsonElement>("{\"name\":\"Alice\"}"));
        Assert.Null(ExpressionEvaluator.Evaluate("{{ $variables.d.missing }}", state));
    }

    [Fact]
    public void Evaluate_VariableIndexOutOfRange_ReturnsNull()
    {
        var state = new FakeWorkflowState();
        state.SetVariable("list", JsonSerializer.Deserialize<JsonElement>("[10]"));
        Assert.Null(ExpressionEvaluator.Evaluate("{{ $variables.list[9] }}", state));
    }

    [Fact]
    public void Evaluate_BareVariable_StillResolvesWholeValue()
    {
        var state = new FakeWorkflowState();
        state.SetVariable("foo", "bar");
        Assert.Equal("bar", ExpressionEvaluator.Evaluate("{{ $variables.foo }}", state));
    }

    [Fact]
    public void Evaluate_NodeOutput_SupportsStringKeyBracket()
    {
        var state = new FakeWorkflowState();
        state.SetNodeOutput(NodeId.Create("n1"), "result",
            new Dictionary<string, object> { ["k"] = "v" });
        Assert.Equal("v", ExpressionEvaluator.Evaluate("{{ $node.n1.output.result[\"k\"] }}", state));
        Assert.Equal("v", ExpressionEvaluator.Evaluate("{{ $node.n1.output.result.k }}", state));
    }

    [Fact]
    public void Evaluate_ResolvesBuiltInFunctions()
    {
        var state = new FakeWorkflowState();

        // 1. coalesce
        Assert.Equal("first", ExpressionEvaluator.Evaluate("{{ coalesce(null, 'first', 'second') }}", state));
        Assert.Equal("second", ExpressionEvaluator.Evaluate("{{ coalesce(null, '', 'second') }}", state));

        // 2. length
        Assert.Equal(5, ExpressionEvaluator.Evaluate("{{ length('hello') }}", state));
        
        // 3. uuid and now
        var uuidResult = ExpressionEvaluator.Evaluate("{{ uuid() }}", state)?.ToString();
        Assert.True(Guid.TryParse(uuidResult, out _));

        var nowResult = ExpressionEvaluator.Evaluate("{{ now() }}", state)?.ToString();
        Assert.True(DateTimeOffset.TryParse(nowResult, out _));
    }
}
