using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Xunit;

namespace Knotarium.Tests.Nodes;

public class InlineCodeNodeTaskTests
{
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    private sealed class StubCredentialAccessor : ICredentialAccessor
    {
        public Task<string?> GetSecretAsync(string credentialRef, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubCapabilityPolicy : ICapabilityPolicy
    {
        private readonly bool _enabled;
        public StubCapabilityPolicy(bool enabled) => _enabled = enabled;
        public Task<bool> IsEnabledAsync(string capability, CancellationToken cancellationToken = default) => Task.FromResult(_enabled);
    }

    private static InlineCodeNodeTask CreateTask(int? timeoutSeconds = null, bool codeEnabled = true) => new(
        new StubHttpClientFactory(),
        new StubCredentialAccessor(),
        NullLogger<InlineCodeNodeTask>.Instance,
        new CSharpScriptCompiler(),
        new StubCapabilityPolicy(codeEnabled),
        timeoutSeconds);

    private static NodeExecutionContext Context(IDictionary<string, object> inputs) => new(
        WorkflowDefinitionId.New(),
        Guid.NewGuid(),
        new NodeId("inline1"),
        new Dictionary<string, object>(inputs),
        new Dictionary<string, object>());

    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsScriptOutputs()
    {
        var task = CreateTask();
        var ctx = Context(new Dictionary<string, object>
        {
            ["language"] = "csharp",
            ["code"] = "return Success(new { sum = 2 + 3 });"
        });

        var result = await task.ExecuteAsync(ctx, CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.NotNull(success.Outputs);
        Assert.True(success.Outputs!.ContainsKey("sum"));
        var sum = Assert.IsType<JsonElement>(success.Outputs["sum"]);
        Assert.Equal(5, sum.GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_WhenCodeCapabilityDisabled_ReturnsFailure_WithoutRunning()
    {
        var task = CreateTask(codeEnabled: false);
        var ctx = Context(new Dictionary<string, object>
        {
            ["code"] = "return Success(new { sum = 2 + 3 });"
        });

        var result = await task.ExecuteAsync(ctx, CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("capability", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_CompileError_ReturnsFailureWithDiagnostics()
    {
        var task = CreateTask();
        var ctx = Context(new Dictionary<string, object>
        {
            ["code"] = "this is not valid c#;"
        });

        var result = await task.ExecuteAsync(ctx, CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("compilation failed", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_InsideSubflow_ScopesVariableAccessByNodeId()
    {
        var task = CreateTask();
        var globals = new Dictionary<string, object>();
        // An inlined subflow node id is prefixed (subflow-a/...). GetVariable/SetVariable from the
        // script should resolve to the instance-scoped key, not the raw name.
        var ctx = new NodeExecutionContext(
            WorkflowDefinitionId.New(),
            Guid.NewGuid(),
            new NodeId("subflow-a/inline-1"),
            new Dictionary<string, object>
            {
                ["language"] = "csharp",
                ["code"] = "context.State.SetVariable(\"counter\", 7); var n = context.State.GetVariable<int>(\"counter\"); return Success(new { n });",
            },
            globals);

        var result = await task.ExecuteAsync(ctx, CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        // Written under the instance-scoped key, and the raw name stays untouched.
        Assert.True(globals.ContainsKey("sf_subflow_a__counter"));
        Assert.False(globals.ContainsKey("counter"));
        // Read-back within the same node resolves the same scope.
        Assert.Equal(7, ((JsonElement)success.Outputs!["n"]).GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_LongRunningScript_IsCancelledByTimeout()
    {
        var task = CreateTask(timeoutSeconds: 1);
        var ctx = Context(new Dictionary<string, object>
        {
            // Observes the ambient cancellationToken provided by the wrapper.
            ["code"] = "await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken); return Success();"
        });

        var result = await task.ExecuteAsync(ctx, CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("timed out", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedLanguage_ReturnsClearFailure()
    {
        var task = CreateTask();
        var ctx = Context(new Dictionary<string, object>
        {
            ["language"] = "javascript",
            ["code"] = "return Success();"
        });

        var result = await task.ExecuteAsync(ctx, CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("not supported", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_FullProgramStyle_WithUsingsAndNoReturn_Compiles()
    {
        var task = CreateTask();
        var ctx = Context(new Dictionary<string, object>
        {
            ["code"] =
                "using System;\n\n" +
                "Console.WriteLine(\"Application started.\");\n\n" +
                "var now = DateTimeOffset.Now;\n" +
                "Console.WriteLine($\"Current time: {now:yyyy-MM-dd HH:mm:ss}\");\n\n" +
                "Console.WriteLine(\"Done.\");"
        });

        var result = await task.ExecuteAsync(ctx, CancellationToken.None);

        // Leading 'using System;' is hoisted, Console resolves, and the missing return is supplied.
        Assert.IsType<LegacyNodeResult.Success>(result);
    }

    // Mirror of the editor's Samples menu (InlineCodeEditorModal.tsx SAMPLES). Keep in sync —
    // these assert the shipped snippets actually compile and run against the wrapper API.
    public static IEnumerable<object[]> SampleSnippets()
    {
        yield return new object[] { "return Success(new { message = \"Hello\", at = DateTimeOffset.UtcNow.ToString(\"o\") });" };
        yield return new object[] { "var name = Input.Get<string>(\"name\") ?? \"world\";\nreturn Success(new { greeting = $\"Hello, {name}!\" });" };
        yield return new object[] { "Logger.LogInformation(\"Inline code ran at {Time}\", DateTimeOffset.UtcNow);\nreturn Success();" };
        yield return new object[] { "var count = context.State.GetVariable<int>(\"count\");\ncount++;\ncontext.State.SetVariable(\"count\", count);\nreturn Success(new { count });" };
        yield return new object[] { "var items = Input.Get<List<int>>(\"items\") ?? new List<int>();\nvar doubled = items.Select(x => x * 2).ToList();\nreturn Success(new { doubled, total = doubled.Sum() });" };
        yield return new object[] { "var value = Input.Get<int>(\"value\");\nif (value < 0)\n    return Fail(\"value must be >= 0\");\nreturn Success(new { value });" };
        yield return new object[] { "var raw = Input.Get<string>(\"payload\") ?? \"{}\";\nusing var doc = JsonDocument.Parse(raw);\nvar keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();\nreturn Success(new { keys });" };
    }

    [Theory]
    [MemberData(nameof(SampleSnippets))]
    public async Task ExecuteAsync_SampleSnippets_CompileAndSucceed(string code)
    {
        var task = CreateTask();
        var ctx = Context(new Dictionary<string, object> { ["code"] = code });

        var result = await task.ExecuteAsync(ctx, CancellationToken.None);

        var msg = result is LegacyNodeResult.Failure f ? f.ErrorMessage : "";
        Assert.True(result is LegacyNodeResult.Success, msg); // msg surfaces a broken sample's diagnostics
    }

    [Fact]
    public async Task ExecuteAsync_GetVariable_ConvertsAcrossStoredRepresentations()
    {
        var task = CreateTask();
        // Simulate a Set Variable node having stored count = "5" (a string) before this node runs.
        var ctx = new NodeExecutionContext(
            WorkflowDefinitionId.New(), Guid.NewGuid(), new NodeId("inline1"),
            new Dictionary<string, object> { ["code"] = "var c = context.State.GetVariable<int>(\"count\"); c++; context.State.SetVariable(\"count\", c); return Success(new { count = c });" },
            new Dictionary<string, object> { ["count"] = "5" });

        var result = await task.ExecuteAsync(ctx, CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal(6, Assert.IsType<JsonElement>(success.Outputs!["count"]).GetInt32());
        // And the increment was written back to the shared global store.
        Assert.Equal(6, Convert.ToInt32(ctx.GlobalVariables["count"]));
    }

    [Fact]
    public async Task SetVariables_SetsMultipleGlobals()
    {
        var globals = new Dictionary<string, object>();
        var rows = JsonSerializer.SerializeToElement(new[]
        {
            new { name = "count", value = "0" },
            new { name = "greeting", value = "hi" },
        });
        var ctx = new NodeExecutionContext(
            WorkflowDefinitionId.New(), Guid.NewGuid(), new NodeId("setvars1"),
            new Dictionary<string, object> { ["variables"] = rows },
            globals);

        var result = await new SetVariablesNodeTask().ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("0", globals["count"]);
        Assert.Equal("hi", globals["greeting"]);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyScript_ReturnsFailure()
    {
        var task = CreateTask();
        var ctx = Context(new Dictionary<string, object> { ["code"] = "   " });

        var result = await task.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<LegacyNodeResult.Failure>(result);
    }
}
