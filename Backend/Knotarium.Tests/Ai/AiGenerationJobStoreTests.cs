using System;
using System.Collections.Generic;
using Knotarium.Api.Services.Ai;
using Knotarium.Features.Ai;
using Knotarium.Core.Domain;
using Xunit;

namespace Knotarium.Tests.Ai;

public class AiGenerationJobStoreTests
{
    private static WorkflowDefinition AnyWorkflow() =>
        new(WorkflowDefinitionId.New(), "wf",
            new[] { new NodeDefinition(NodeId.Create("t"), "manualTrigger", new Dictionary<string, object>()) },
            Array.Empty<EdgeDefinition>());

    [Fact]
    public void Create_StartsQueued_AndIsRetrievable()
    {
        var store = new AiGenerationJobStore();
        var job = store.Create("do a thing");

        Assert.Equal(AiGenerationStatus.Queued, job.Status);
        Assert.Equal("do a thing", job.Intent);
        Assert.NotNull(store.Get(job.Id));
        Assert.Null(store.Get("nope"));
    }

    [Fact]
    public void MarkSucceeded_RecordsWorkflowSlotsAndAdvancesTimestamp()
    {
        var t = DateTimeOffset.UnixEpoch;
        var store = new AiGenerationJobStore(() => t);
        var job = store.Create("x");

        t = t.AddSeconds(5);
        store.MarkRunning(job.Id);
        t = t.AddSeconds(5);
        store.MarkSucceeded(job.Id, AnyWorkflow(), new[] { "weather-api" }, attempts: 2);

        var final = store.Get(job.Id)!;
        Assert.Equal(AiGenerationStatus.Succeeded, final.Status);
        Assert.NotNull(final.Workflow);
        Assert.Equal(new[] { "weather-api" }, final.OpenSlots);
        Assert.Equal(2, final.Attempts);
        Assert.True(final.UpdatedAt > final.CreatedAt);
    }

    [Fact]
    public void MarkFailed_WithDiagnostics_AndWithError_AreBothRecorded()
    {
        var store = new AiGenerationJobStore();

        var a = store.Create("a");
        store.MarkFailed(a.Id, new[] { "ERR_CYCLE_DETECTED: ..." }, attempts: 3);
        Assert.Equal(AiGenerationStatus.Failed, store.Get(a.Id)!.Status);
        Assert.Contains("ERR_CYCLE_DETECTED: ...", store.Get(a.Id)!.Diagnostics);

        var b = store.Create("b");
        store.MarkFailed(b.Id, "Anthropic API returned 401");
        Assert.Equal(AiGenerationStatus.Failed, store.Get(b.Id)!.Status);
        Assert.Equal("Anthropic API returned 401", store.Get(b.Id)!.Error);
    }
}
