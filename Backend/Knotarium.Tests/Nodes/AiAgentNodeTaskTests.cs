using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.Ai;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Xunit;

namespace Knotarium.Tests.Nodes;

public class AiAgentNodeTaskTests
{
    // --- Fakes ---

    private sealed class ScriptedAgentChat : IAgentChatService
    {
        private readonly Queue<AgentTurnResult> _turns;
        public List<AgentChatRequest> Requests { get; } = new();
        public ScriptedAgentChat(params AgentTurnResult[] turns) => _turns = new Queue<AgentTurnResult>(turns);
        public Task<AgentTurnResult> CompleteTurnAsync(AgentChatRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(_turns.Dequeue());
        }
    }

    private sealed class ThrowingAgentChat : IAgentChatService
    {
        public Task<AgentTurnResult> CompleteTurnAsync(AgentChatRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("provider boom");
    }

    private sealed class FakeToolRunner : IAgentToolRunner
    {
        private readonly Queue<AgentToolResult> _results;
        public List<AgentToolInvocation> Invocations { get; } = new();
        public FakeToolRunner(params AgentToolResult[] results) => _results = new Queue<AgentToolResult>(results);
        public Task<AgentToolResult> RunToolAsync(AgentToolInvocation invocation, CancellationToken ct = default)
        {
            Invocations.Add(invocation);
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : new AgentToolResult(true, "{}", Guid.NewGuid()));
        }
    }

    private sealed class NeverToolRunner : IAgentToolRunner
    {
        public Task<AgentToolResult> RunToolAsync(AgentToolInvocation invocation, CancellationToken ct = default)
            => throw new Xunit.Sdk.XunitException("the tool runner must not be called for this case.");
    }

    private sealed class StubCapabilityPolicy : ICapabilityPolicy
    {
        private readonly bool _enabled;
        public StubCapabilityPolicy(bool enabled) => _enabled = enabled;
        public Task<bool> IsEnabledAsync(string capability, CancellationToken ct = default) => Task.FromResult(_enabled);
    }

    // --- Helpers ---

    private static JsonElement Json(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static AgentTurnResult FinalTurn(string text, int input = 5, int output = 5) =>
        new(text, Array.Empty<AgentToolCall>(), input, output);

    private static AgentTurnResult ToolTurn(string toolName, string argsJson, int input = 5, int output = 5) =>
        new(null, new[] { new AgentToolCall("call-1", toolName, Json(argsJson)) }, input, output);

    private const string OneTool = """
        [ { "workflowId": "wf-lookup", "name": "lookup", "description": "look up a customer",
            "parameters": [ { "name": "id", "type": "string", "required": true } ],
            "outputs": [ "customer" ] } ]
        """;

    private static NodeExecutionContext Context(Dictionary<string, object> inputs) => new(
        WorkflowId: WorkflowDefinitionId.New(),
        ExecutionId: Guid.NewGuid(),
        NodeId: NodeId.Create("agent-1"),
        Inputs: inputs,
        GlobalVariables: new Dictionary<string, object>());

    private static AiAgentNodeTask MakeAgent(IAgentChatService chat, IAgentToolRunner runner, bool capability = true) =>
        new(chat, runner, new StubCapabilityPolicy(capability));

    // --- Capability + input guards ---

    [Fact]
    public async Task Capability_off_fails_before_calling_the_model()
    {
        var chat = new ScriptedAgentChat(); // a call would throw (empty queue)
        var task = MakeAgent(chat, new NeverToolRunner(), capability: false);
        var result = await task.ExecuteAsync(Context(new() { ["task"] = "hi" }), CancellationToken.None);
        Assert.Contains("'aiAgent' capability is off", Assert.IsType<LegacyNodeResult.Failure>(result).ErrorMessage);
        Assert.Empty(chat.Requests);
    }

    [Fact]
    public async Task Missing_task_fails()
    {
        var task = MakeAgent(new ScriptedAgentChat(), new NeverToolRunner());
        var result = await task.ExecuteAsync(Context(new()), CancellationToken.None);
        Assert.Contains("missing required 'task'", Assert.IsType<LegacyNodeResult.Failure>(result).ErrorMessage);
    }

    // --- Loop behaviour ---

    [Fact]
    public async Task Direct_answer_no_tools_succeeds()
    {
        var chat = new ScriptedAgentChat(FinalTurn("the answer is 42"));
        var result = await MakeAgent(chat, new NeverToolRunner()).ExecuteAsync(
            Context(new() { ["task"] = "what is the answer" }), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("the answer is 42", success.Outputs!["result"]);
        Assert.Single(chat.Requests);
    }

    [Fact]
    public async Task Tool_call_runs_the_tool_then_answers()
    {
        var chat = new ScriptedAgentChat(
            ToolTurn("lookup", """{ "id": "c-7" }"""),
            FinalTurn("done"));
        var runner = new FakeToolRunner(new AgentToolResult(true, """{ "customer": "Acme" }""", Guid.NewGuid()));

        var result = await MakeAgent(chat, runner).ExecuteAsync(
            Context(new() { ["task"] = "look up c-7", ["tools"] = OneTool }), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("done", success.Outputs!["result"]);

        // The tool was invoked with validated args + the binding's target/outputs.
        var call = Assert.Single(runner.Invocations);
        Assert.Equal("wf-lookup", call.WorkflowId);
        Assert.Equal("lookup", call.ToolName);
        Assert.Equal("c-7", call.Arguments["id"]);
        Assert.Equal(new[] { "customer" }, call.Outputs);
        Assert.Equal(1, call.Iteration);

        // Second turn's transcript carries the assistant tool call + the tool result.
        var secondTurn = chat.Requests[1].Messages;
        Assert.Contains(secondTurn, m => m.Role == AgentRoles.Assistant && m.ToolCalls is { Count: 1 });
        Assert.Contains(secondTurn, m => m.Role == AgentRoles.Tool && m.ToolCallId == "call-1");
    }

    [Fact]
    public async Task Unknown_tool_is_reported_to_the_model_without_running_anything()
    {
        var chat = new ScriptedAgentChat(
            ToolTurn("does_not_exist", "{}"),
            FinalTurn("ok"));

        var result = await MakeAgent(chat, new NeverToolRunner()).ExecuteAsync(
            Context(new() { ["task"] = "go", ["tools"] = OneTool }), CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Success>(result);
        var toolMsg = chat.Requests[1].Messages.Single(m => m.Role == AgentRoles.Tool);
        Assert.Contains("unknown tool", toolMsg.ToolResultJson);
    }

    [Fact]
    public async Task Invalid_arguments_are_reported_to_the_model_without_running_the_tool()
    {
        // 'id' is required + string; the model sends a number → validation error, tool never runs.
        var chat = new ScriptedAgentChat(
            ToolTurn("lookup", """{ "id": 5 }"""),
            FinalTurn("recovered"));

        var result = await MakeAgent(chat, new NeverToolRunner()).ExecuteAsync(
            Context(new() { ["task"] = "go", ["tools"] = OneTool }), CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Success>(result);
        var toolMsg = chat.Requests[1].Messages.Single(m => m.Role == AgentRoles.Tool);
        Assert.Contains("must be a string", toolMsg.ToolResultJson);
    }

    [Fact]
    public async Task Failed_tool_run_is_fed_back_and_the_loop_continues()
    {
        var chat = new ScriptedAgentChat(
            ToolTurn("lookup", """{ "id": "x" }"""),
            FinalTurn("gave up gracefully"));
        var runner = new FakeToolRunner(new AgentToolResult(false, """{ "error": "boom" }""", Guid.NewGuid(), "boom"));

        var result = await MakeAgent(chat, runner).ExecuteAsync(
            Context(new() { ["task"] = "go", ["tools"] = OneTool }), CancellationToken.None);

        Assert.Equal("gave up gracefully", Assert.IsType<LegacyNodeResult.Success>(result).Outputs!["result"]);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task Iteration_cap_fails_the_node()
    {
        // Always asks for a tool → never a final answer.
        var chat = new ScriptedAgentChat(
            ToolTurn("lookup", """{ "id": "a" }"""),
            ToolTurn("lookup", """{ "id": "b" }"""));
        var runner = new FakeToolRunner(
            new AgentToolResult(true, "{}", Guid.NewGuid()),
            new AgentToolResult(true, "{}", Guid.NewGuid()));

        var result = await MakeAgent(chat, runner).ExecuteAsync(
            Context(new() { ["task"] = "go", ["tools"] = OneTool, ["maxIterations"] = "2" }), CancellationToken.None);

        Assert.Contains("maximum of 2 iterations", Assert.IsType<LegacyNodeResult.Failure>(result).ErrorMessage);
    }

    [Fact]
    public async Task Token_budget_exceeded_fails_the_node()
    {
        var chat = new ScriptedAgentChat(FinalTurn("hi", input: 60, output: 60));
        var result = await MakeAgent(chat, new NeverToolRunner()).ExecuteAsync(
            Context(new() { ["task"] = "go", ["tokenBudget"] = "100" }), CancellationToken.None);
        Assert.Contains("token budget of 100 exceeded", Assert.IsType<LegacyNodeResult.Failure>(result).ErrorMessage);
    }

    [Fact]
    public async Task ResultSchema_emits_a_parsed_object()
    {
        var chat = new ScriptedAgentChat(FinalTurn("""{ "score": 9 }"""));
        var result = await MakeAgent(chat, new NeverToolRunner()).ExecuteAsync(
            Context(new() { ["task"] = "rate it", ["resultSchema"] = """{ "type": "object" }""" }), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        var parsed = Assert.IsType<JsonElement>(success.Outputs!["result"]);
        Assert.Equal(9, parsed.GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task ResultSchema_reasks_once_on_non_json()
    {
        var chat = new ScriptedAgentChat(
            FinalTurn("not json at all"),
            FinalTurn("""{ "ok": true }"""));
        var result = await MakeAgent(chat, new NeverToolRunner()).ExecuteAsync(
            Context(new() { ["task"] = "go", ["resultSchema"] = """{ "type": "object" }""" }), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.True(Assert.IsType<JsonElement>(success.Outputs!["result"]).GetProperty("ok").GetBoolean());
        Assert.Equal(2, chat.Requests.Count);
    }

    [Fact]
    public async Task Provider_error_becomes_a_node_failure()
    {
        var result = await MakeAgent(new ThrowingAgentChat(), new NeverToolRunner()).ExecuteAsync(
            Context(new() { ["task"] = "go" }), CancellationToken.None);
        Assert.Contains("provider boom", Assert.IsType<LegacyNodeResult.Failure>(result).ErrorMessage);
    }

    // --- Tools parsing ---

    [Fact]
    public void TryParseTools_reads_a_valid_binding()
    {
        Assert.True(AiAgentNodeTask.TryParseTools(OneTool, out var bindings, out _));
        var b = Assert.Single(bindings);
        Assert.Equal("lookup", b.Name);
        Assert.Equal("wf-lookup", b.WorkflowId);
        Assert.Equal("id", Assert.Single(b.Parameters).Name);
        Assert.True(b.Parameters[0].Required);
        Assert.Equal("customer", Assert.Single(b.Outputs));
    }

    [Fact]
    public void TryParseTools_allows_empty()
    {
        Assert.True(AiAgentNodeTask.TryParseTools(null, out var bindings, out _));
        Assert.Empty(bindings);
    }

    [Theory]
    [InlineData("""[ { "name": "a", "workflowId": "" } ]""", "no workflowId")]
    [InlineData("""[ { "name": "bad name", "workflowId": "w" } ]""", "invalid")]
    [InlineData("""[ { "name": "a", "workflowId": "w" }, { "name": "a", "workflowId": "w" } ]""", "duplicate")]
    [InlineData("""{ "not": "an array" }""", "must be a JSON array")]
    public void TryParseTools_rejects_invalid(string raw, string expectedFragment)
    {
        Assert.False(AiAgentNodeTask.TryParseTools(raw, out _, out var error));
        Assert.Contains(expectedFragment, error);
    }

    [Theory]
    [InlineData("lookup_customer", true)]
    [InlineData("Tool1", true)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    public void IsValidToolName_enforces_the_charset(string name, bool valid)
    {
        Assert.Equal(valid, AiAgentNodeTask.IsValidToolName(name));
    }

    // --- Argument validation ---

    [Fact]
    public void TryValidateArgs_coerces_by_declared_type_and_drops_unknown_keys()
    {
        AiAgentNodeTask.TryParseTools("""
            [ { "workflowId": "w", "name": "t", "parameters": [
                { "name": "s", "type": "string", "required": true },
                { "name": "n", "type": "number" },
                { "name": "b", "type": "boolean" } ] } ]
            """, out var bindings, out _);

        var ok = AiAgentNodeTask.TryValidateArgs(bindings[0],
            Json("""{ "s": "hi", "n": 3, "b": true, "extra": "dropped" }"""), out var validated, out var error);

        Assert.True(ok, error);
        Assert.Equal("hi", validated["s"]);
        Assert.Equal(3d, validated["n"]);
        Assert.Equal(true, validated["b"]);
        Assert.False(validated.ContainsKey("extra"));
    }

    [Fact]
    public void TryValidateArgs_requires_required_params()
    {
        AiAgentNodeTask.TryParseTools(OneTool, out var bindings, out _);
        Assert.False(AiAgentNodeTask.TryValidateArgs(bindings[0], Json("{}"), out _, out var error));
        Assert.Contains("missing required argument 'id'", error);
    }

    [Fact]
    public void TryValidateArgs_type_mismatch_is_rejected_not_coerced()
    {
        AiAgentNodeTask.TryParseTools("""[ { "workflowId": "w", "name": "t", "parameters": [ { "name": "n", "type": "number", "required": true } ] } ]""",
            out var bindings, out _);
        Assert.False(AiAgentNodeTask.TryValidateArgs(bindings[0], Json("""{ "n": "five" }"""), out _, out var error));
        Assert.Contains("must be a number", error);
    }
}
