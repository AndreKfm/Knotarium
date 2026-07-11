using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Xunit;

namespace Knotarium.Tests.Nodes.Condition;

/// <summary>
/// Integration tests for the rewritten <see cref="ConditionNodeTask"/> over the new <c>logic</c> blob:
/// status→port routing, and task-side ref resolution (D7) — the centerpiece being that a MISSING ref
/// is RESOLUTION_FAILED (fail-node) while a resolved <c>null</c> is a legitimate value.
/// </summary>
public class ConditionNodeTaskLogicTests
{
    // A state stub whose TryResolveVariable reports found-ness: a present key (even with null value)
    // is "resolved"; an absent key is "missing".
    private sealed class FakeState : IWorkflowState
    {
        private readonly Dictionary<string, object?> _vars;
        public FakeState(Dictionary<string, object?> vars) => _vars = vars;
        public T? GetVariable<T>(string name) => _vars.TryGetValue(name, out var v) && v is T t ? t : default;
        public void SetVariable(string name, object? value) => _vars[name] = value;
        public JsonElement? GetNodeOutput(NodeId nodeId, string outputName) => null;
        public bool TryResolveVariable(string name, out object? value) => _vars.TryGetValue(name, out value);
    }

    private static NodeExecutionContext Ctx(string logicJson, IWorkflowState? state = null)
    {
        var inputs = new Dictionary<string, object> { ["logic"] = JsonDocument.Parse(logicJson).RootElement.Clone() };
        return new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("cond"),
            Inputs: inputs,
            GlobalVariables: new Dictionary<string, object>(),
            State: state);
    }

    private static async Task<string> Port(NodeExecutionContext ctx)
    {
        var result = await new ConditionNodeTask().ExecuteAsync(ctx, CancellationToken.None);
        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        return (string)success.Outputs!["selectedPort"];
    }

    private static async Task<string> FailMessage(NodeExecutionContext ctx)
    {
        var result = await new ConditionNodeTask().ExecuteAsync(ctx, CancellationToken.None);
        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        return failure.ErrorMessage;
    }

    private static async Task<LegacyNodeResult.Failure> FailResult(NodeExecutionContext ctx)
    {
        var result = await new ConditionNodeTask().ExecuteAsync(ctx, CancellationToken.None);
        return Assert.IsType<LegacyNodeResult.Failure>(result);
    }

    private const string Lit5EqLit5 =
        """{"version":1,"comb":"and","cmps":[{"id":"c1","op":"eq","a":{"kind":"lit","type":"number","value":5},"b":{"kind":"lit","type":"number","value":5}}]}""";
    private const string Lit5EqLit6 =
        """{"version":1,"comb":"and","cmps":[{"id":"c1","op":"eq","a":{"kind":"lit","type":"number","value":5},"b":{"kind":"lit","type":"number","value":6}}]}""";
    private const string RefXEqLit5 =
        """{"version":1,"comb":"and","cmps":[{"id":"c1","op":"eq","a":{"kind":"ref","type":"number","ref":{"__type":"variable_ref","variableName":"x"}},"b":{"kind":"lit","type":"number","value":5}}]}""";
    private const string RefXNumberEqLit1 =
        """{"version":1,"comb":"and","cmps":[{"id":"c1","op":"eq","a":{"kind":"ref","type":"number","ref":{"__type":"variable_ref","variableName":"x"}},"b":{"kind":"lit","type":"number","value":1}}]}""";
    private const string RefXExists =
        """{"version":1,"comb":"and","cmps":[{"id":"c1","op":"exists","a":{"kind":"ref","type":"string","ref":{"__type":"variable_ref","variableName":"x"}}}]}""";
    private const string RefXNexists =
        """{"version":1,"comb":"and","cmps":[{"id":"c1","op":"nexists","a":{"kind":"ref","type":"string","ref":{"__type":"variable_ref","variableName":"x"}}}]}""";
    private const string GtNumVsStr =
        """{"version":1,"comb":"and","cmps":[{"id":"c1","op":"gt","a":{"kind":"lit","type":"number","value":5},"b":{"kind":"lit","type":"string","value":"3"}}]}""";
    private const string BadVersion =
        """{"version":2,"comb":"and","cmps":[{"id":"c1","op":"eq","a":{"kind":"lit","type":"number","value":1},"b":{"kind":"lit","type":"number","value":1}}]}""";

    [Fact]
    public async Task Lit_equals_routes_true() => Assert.Equal("true", await Port(Ctx(Lit5EqLit5)));

    [Fact]
    public async Task Lit_not_equals_routes_false() => Assert.Equal("false", await Port(Ctx(Lit5EqLit6)));

    [Fact]
    public async Task Resolved_ref_is_used()
    {
        var state = new FakeState(new() { ["x"] = 5.0 });
        Assert.Equal("true", await Port(Ctx(RefXEqLit5, state)));
    }

    [Fact]
    public async Task Missing_ref_fails_the_node_with_resolution_failed()
    {
        var message = await FailMessage(Ctx(RefXEqLit5, new FakeState(new())));
        Assert.Contains("RESOLUTION_FAILED", message);
        Assert.Contains("c1", message);
        Assert.Contains("'a'", message);
    }

    [Fact]
    public async Task Resolved_null_is_legitimate_for_existence_ops()
    {
        var state = new FakeState(new() { ["x"] = null }); // present but null
        Assert.Equal("true", await Port(Ctx(RefXNexists, state)));
        Assert.Equal("false", await Port(Ctx(RefXExists, state)));
    }

    [Fact]
    public async Task Missing_ref_is_not_rescued_by_exists()
    {
        var message = await FailMessage(Ctx(RefXExists, new FakeState(new())));
        Assert.Contains("RESOLUTION_FAILED", message);
    }

    [Fact]
    public async Task Uncoercible_resolved_value_is_coercion_failed()
    {
        var state = new FakeState(new() { ["x"] = "abc" });
        var message = await FailMessage(Ctx(RefXNumberEqLit1, state));
        Assert.Contains("COERCION_FAILED", message);
    }

    [Fact]
    public async Task Cross_type_ordering_is_type_mismatch()
    {
        var message = await FailMessage(Ctx(GtNumVsStr));
        Assert.Contains("TYPE_MISMATCH", message);
    }

    [Fact]
    public async Task Malformed_logic_fails_the_node_with_invalid_logic()
    {
        var message = await FailMessage(Ctx(BadVersion));
        Assert.Contains("INVALID_LOGIC", message);
    }

    [Fact]
    public async Task Failure_carries_a_structured_error_code_not_just_the_message_string()
    {
        // R6: the code lives in a discrete field (queryable downstream by the audit), in ADDITION to the
        // [CODE]-prefixed human message. A missing ref is the representative case.
        var failure = await FailResult(Ctx(RefXEqLit5, new FakeState(new())));
        Assert.Equal("RESOLUTION_FAILED", failure.ErrorCode);
        Assert.Contains("RESOLUTION_FAILED", failure.ErrorMessage); // message still human-readable

        // And a different code routes through the same field.
        var mismatch = await FailResult(Ctx(GtNumVsStr));
        Assert.Equal("TYPE_MISMATCH", mismatch.ErrorCode);
    }

    [Fact]
    public async Task And_combinator_requires_all_true()
    {
        const string both =
            """{"version":1,"comb":"and","cmps":[{"id":"c1","op":"eq","a":{"kind":"lit","type":"number","value":1},"b":{"kind":"lit","type":"number","value":1}},{"id":"c2","op":"eq","a":{"kind":"lit","type":"number","value":2},"b":{"kind":"lit","type":"number","value":VAL}}]}""";
        Assert.Equal("true", await Port(Ctx(both.Replace("VAL", "2"))));
        Assert.Equal("false", await Port(Ctx(both.Replace("VAL", "9"))));
    }

    [Fact]
    public async Task Or_combinator_needs_one_true()
    {
        const string logic =
            """{"version":1,"comb":"or","cmps":[{"id":"c1","op":"eq","a":{"kind":"lit","type":"number","value":1},"b":{"kind":"lit","type":"number","value":9}},{"id":"c2","op":"eq","a":{"kind":"lit","type":"number","value":2},"b":{"kind":"lit","type":"number","value":2}}]}""";
        Assert.Equal("true", await Port(Ctx(logic)));
    }

    // ── v2 tree (Phase 8) end-to-end through the task ──

    private const string TreeAndNot =
        """{"version":2,"root":{"kind":"group","id":"g","op":"and","children":[{"kind":"cmp","id":"c1","op":"eq","a":{"kind":"lit","type":"number","value":1},"b":{"kind":"lit","type":"number","value":1}},{"kind":"not","id":"n1","child":{"kind":"cmp","id":"c2","op":"eq","a":{"kind":"lit","type":"number","value":1},"b":{"kind":"lit","type":"number","value":VAL}}}]}}""";

    [Fact]
    public async Task V2_tree_with_not_routes_true_when_inner_is_false()
    {
        // True AND NOT(1 eq 2 = False) = True AND True = True.
        Assert.Equal("true", await Port(Ctx(TreeAndNot.Replace("VAL", "2"))));
    }

    [Fact]
    public async Task V2_tree_with_not_routes_false_when_inner_is_true()
    {
        // True AND NOT(1 eq 1 = True) = True AND False = False.
        Assert.Equal("false", await Port(Ctx(TreeAndNot.Replace("VAL", "1"))));
    }

    [Fact]
    public async Task Missing_ref_in_a_deep_leaf_fails_resolution_and_surfaces_the_leaf_id()
    {
        // OR group: c1 is True, but a ref in a NOT-wrapped deep leaf is missing → RESOLUTION_FAILED
        // dominates (B9 Error-over-True) and bubbles up the deep leaf's id (D7 resolution recurses).
        const string tree =
            """{"version":2,"root":{"kind":"group","id":"g","op":"or","children":[{"kind":"cmp","id":"c1","op":"eq","a":{"kind":"lit","type":"number","value":1},"b":{"kind":"lit","type":"number","value":1}},{"kind":"not","id":"n1","child":{"kind":"cmp","id":"deep","op":"eq","a":{"kind":"ref","type":"number","ref":{"__type":"variable_ref","variableName":"x"}},"b":{"kind":"lit","type":"number","value":5}}}]}}""";
        var message = await FailMessage(Ctx(tree, new FakeState(new())));
        Assert.Contains("RESOLUTION_FAILED", message);
        Assert.Contains("deep", message);
    }

    [Fact]
    public async Task Empty_logic_string_falls_through_to_unconfigured_false()
    {
        var inputs = new Dictionary<string, object> { ["logic"] = "   " };
        var ctx = new NodeExecutionContext(
            WorkflowDefinitionId.New(), Guid.NewGuid(), NodeId.Create("cond"),
            inputs, new Dictionary<string, object>());
        Assert.Equal("false", await Port(ctx));
    }
}
