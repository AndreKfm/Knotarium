using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Api.Services.Ai;
using KnotGarden.Features.Ai;
using KnotGarden.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnotGarden.Tests.Ai;

public class AiGenerationWorkerTests
{
    private sealed class FakeRunner : IAiGenerationRunner
    {
        private readonly Func<string, AiGenerationRunResult> _run;
        public FakeRunner(Func<string, AiGenerationRunResult> run) => _run = run;
        public Task<AiGenerationRunResult> RunAsync(string intent, CancellationToken cancellationToken = default, KnotGarden.Core.Domain.WorkflowDefinition? currentWorkflow = null)
            => Task.FromResult(_run(intent));
    }

    private sealed class ThrowingRunner : IAiGenerationRunner
    {
        public Task<AiGenerationRunResult> RunAsync(string intent, CancellationToken cancellationToken = default, KnotGarden.Core.Domain.WorkflowDefinition? currentWorkflow = null)
            => throw new InvalidOperationException("Anthropic API returned 401");
    }

    private static WorkflowDefinition AnyWorkflow() =>
        new(WorkflowDefinitionId.New(), "wf",
            new[] { new NodeDefinition(NodeId.Create("t"), "manualTrigger", new Dictionary<string, object>()) },
            Array.Empty<EdgeDefinition>());

    private static AiGenerationWorker BuildWorker(IAiGenerationRunner runner, AiGenerationJobStore store, AiGenerationQueue queue)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => runner);
        var provider = services.BuildServiceProvider();
        return new AiGenerationWorker(queue, store, provider, NullLogger<AiGenerationWorker>.Instance);
    }

    [Fact]
    public async Task ProcessAsync_SuccessfulRun_MarksJobSucceeded()
    {
        var store = new AiGenerationJobStore();
        var job = store.Create("build something");
        var runner = new FakeRunner(_ => new AiGenerationRunResult(true, AnyWorkflow(), new[] { "api-key" }, Array.Empty<string>(), 1));
        var worker = BuildWorker(runner, store, new AiGenerationQueue());

        await worker.ProcessAsync(job.Id, CancellationToken.None);

        var final = store.Get(job.Id)!;
        Assert.Equal(AiGenerationStatus.Succeeded, final.Status);
        Assert.NotNull(final.Workflow);
        Assert.Equal(new[] { "api-key" }, final.OpenSlots);
    }

    [Fact]
    public async Task ProcessAsync_GiveUpRun_MarksJobFailedWithDiagnostics()
    {
        var store = new AiGenerationJobStore();
        var job = store.Create("build something");
        var runner = new FakeRunner(_ => new AiGenerationRunResult(false, null, Array.Empty<string>(),
            new[] { "ERR_INVALID_NODE_TYPE: ..." }, 3));
        var worker = BuildWorker(runner, store, new AiGenerationQueue());

        await worker.ProcessAsync(job.Id, CancellationToken.None);

        var final = store.Get(job.Id)!;
        Assert.Equal(AiGenerationStatus.Failed, final.Status);
        Assert.Contains("ERR_INVALID_NODE_TYPE: ...", final.Diagnostics);
    }

    [Fact]
    public async Task ProcessAsync_RunnerThrows_MarksJobFailedWithError_AndDoesNotThrow()
    {
        var store = new AiGenerationJobStore();
        var job = store.Create("build something");
        var worker = BuildWorker(new ThrowingRunner(), store, new AiGenerationQueue());

        await worker.ProcessAsync(job.Id, CancellationToken.None); // must not throw

        var final = store.Get(job.Id)!;
        Assert.Equal(AiGenerationStatus.Failed, final.Status);
        Assert.Equal("Anthropic API returned 401", final.Error);
    }

    [Fact]
    public async Task ProcessAsync_UnknownJobId_IsNoOp()
    {
        var store = new AiGenerationJobStore();
        var worker = BuildWorker(new ThrowingRunner(), store, new AiGenerationQueue());

        await worker.ProcessAsync("does-not-exist", CancellationToken.None); // must not throw
    }
}
