using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.Ai;
using Knotarium.Core.Domain;
using Knotarium.Features.Ai;
using Knotarium.Features.Compiler;
using Knotarium.Tests.Compiler;
using Xunit;

namespace Knotarium.Tests.Ai;

public class WorkflowGenerationOrchestratorTests
{
    /// <summary>A generator scripted to return a queue of attempts, recording each request it receives.</summary>
    private sealed class ScriptedGenerator : IWorkflowGenerator
    {
        private readonly Queue<WorkflowGenerationAttempt> _attempts;
        public List<WorkflowGenerationRequest> Requests { get; } = new();
        public ScriptedGenerator(params WorkflowGenerationAttempt[] attempts) => _attempts = new(attempts);

        public Task<WorkflowGenerationAttempt> GenerateAsync(WorkflowGenerationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_attempts.Dequeue());
        }
    }

    private static IReadOnlyList<NodePackageManifest> Catalog() =>
        new List<NodePackageManifest>(new InMemoryNodePackageManifestProvider().GetAllManifests());

    private static WorkflowCompiler Compiler() =>
        new(new MockWorkflowDefinitionProvider(), new InMemoryNodePackageManifestProvider());

    private static WorkflowDefinition ValidWorkflow()
    {
        var trigger = new NodeDefinition(NodeId.Create("t"), "manualTrigger", new Dictionary<string, object>());
        var log = new NodeDefinition(NodeId.Create("l"), "log", new Dictionary<string, object> { ["message"] = "hi" });
        var edge = new EdgeDefinition("e1", trigger.Id, "result", log.Id, "in");
        return new WorkflowDefinition(WorkflowDefinitionId.New(), "Valid", new[] { trigger, log }, new[] { edge });
    }

    private static WorkflowDefinition InvalidTypeWorkflow()
    {
        // 'notARealNode' is not in the manifest → ERR_INVALID_NODE_TYPE.
        var bad = new NodeDefinition(NodeId.Create("x"), "notARealNode", new Dictionary<string, object>());
        return new WorkflowDefinition(WorkflowDefinitionId.New(), "Bad", new[] { bad }, Array.Empty<EdgeDefinition>());
    }

    private static WorkflowGenerationAttempt Parsed(WorkflowDefinition wf) => new(wf, "{...}", null);
    private static WorkflowGenerationAttempt Unparsed(string error) => new(null, "garbage", error);

    [Fact]
    public async Task GenerateAsync_RepairsAfterCompileError_AndThreadsErrCodeBack()
    {
        // Attempt 1: compiles to an invalid node type. Attempt 2: valid.
        var generator = new ScriptedGenerator(Parsed(InvalidTypeWorkflow()), Parsed(ValidWorkflow()));
        var orchestrator = new WorkflowGenerationOrchestrator(generator, Compiler(), new AiGenerationOptions());

        var outcome = await orchestrator.GenerateAsync("build something", Catalog());

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, outcome.Attempts);
        Assert.NotNull(outcome.Workflow);

        // The second request must carry the first compile's ERR_* code as prior errors.
        Assert.Equal(2, generator.Requests.Count);
        Assert.Null(generator.Requests[0].PriorErrors);
        Assert.NotNull(generator.Requests[1].PriorErrors);
        Assert.Contains(generator.Requests[1].PriorErrors!, e => e.Contains("ERR_INVALID_NODE_TYPE"));
    }

    [Fact]
    public async Task GenerateAsync_RepairsAfterParseError()
    {
        var generator = new ScriptedGenerator(Unparsed("Output was not valid JSON"), Parsed(ValidWorkflow()));
        var orchestrator = new WorkflowGenerationOrchestrator(generator, Compiler(), new AiGenerationOptions());

        var outcome = await orchestrator.GenerateAsync("build something", Catalog());

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, outcome.Attempts);
        Assert.Contains(generator.Requests[1].PriorErrors!, e => e.Contains("not valid JSON"));
    }

    [Fact]
    public async Task GenerateAsync_GivesUpAfterMaxAttempts_WithLastDiagnostics()
    {
        var generator = new ScriptedGenerator(
            Parsed(InvalidTypeWorkflow()), Parsed(InvalidTypeWorkflow()), Parsed(InvalidTypeWorkflow()));
        var options = new AiGenerationOptions { MaxRepairAttempts = 3 };
        var orchestrator = new WorkflowGenerationOrchestrator(generator, Compiler(), options);

        var outcome = await orchestrator.GenerateAsync("build something", Catalog());

        Assert.False(outcome.Succeeded);
        Assert.Equal(3, outcome.Attempts);
        Assert.Null(outcome.Workflow);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("ERR_INVALID_NODE_TYPE"));
    }

    [Fact]
    public async Task GenerateAsync_SucceedsFirstTry_NoRepairNeeded()
    {
        var generator = new ScriptedGenerator(Parsed(ValidWorkflow()));
        var orchestrator = new WorkflowGenerationOrchestrator(generator, Compiler(), new AiGenerationOptions());

        var outcome = await orchestrator.GenerateAsync("build something", Catalog());

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.Attempts);
        Assert.Single(generator.Requests);
    }
}
