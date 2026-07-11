using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Nodes;
using Xunit;

namespace KnotGarden.Tests.Nodes;

public class DelayNodeTaskTests
{
    [Fact]
    public async Task DelayNodeTask_WaitsSpecifiedMilliseconds()
    {
        // Arrange
        var task = new DelayNodeTask();
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("delay-1"),
            Inputs: new Dictionary<string, object>
            {
                ["delayMs"] = 100.0
            },
            GlobalVariables: new Dictionary<string, object>()
        );

        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        stopwatch.Stop();
        Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.True(stopwatch.ElapsedMilliseconds >= 90, $"Elapsed was only {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task DelayNodeTask_WaitsSpecifiedDurationString()
    {
        // Arrange
        var task = new DelayNodeTask();
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("delay-2"),
            Inputs: new Dictionary<string, object>
            {
                ["duration"] = "00:00:00.100"
            },
            GlobalVariables: new Dictionary<string, object>()
        );

        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = await task.ExecuteAsync(context, CancellationToken.None);

        // Assert
        stopwatch.Stop();
        Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.True(stopwatch.ElapsedMilliseconds >= 90, $"Elapsed was only {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task DelayNodeTask_HonorsCancellationToken()
    {
        // Arrange
        var task = new DelayNodeTask();
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("delay-3"),
            Inputs: new Dictionary<string, object>
            {
                ["delayMs"] = 500.0 // Sub-second: blocks inline, so cancellation is honored at the node.
            },
            GlobalVariables: new Dictionary<string, object>()
        );

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50); // Cancel quickly

        // Act
        var result = await task.ExecuteAsync(context, cts.Token);

        // Assert
        Assert.IsType<LegacyNodeResult.Failure>(result);
    }

    [Fact]
    public async Task DelayNodeTask_LongDelay_SuspendsWithoutBlocking()
    {
        // A non-trivial delay must NOT block the worker — it returns a Delay result so the engine can
        // schedule a timed resume. The call returns immediately (no 5s wait) carrying the duration.
        var task = new DelayNodeTask();
        var context = new NodeExecutionContext(
            WorkflowId: WorkflowDefinitionId.New(),
            ExecutionId: Guid.NewGuid(),
            NodeId: NodeId.Create("delay-4"),
            Inputs: new Dictionary<string, object> { ["delayMs"] = 5000.0 },
            GlobalVariables: new Dictionary<string, object>());

        var stopwatch = Stopwatch.StartNew();
        var result = await task.ExecuteAsync(context, CancellationToken.None);
        stopwatch.Stop();

        var delay = Assert.IsType<LegacyNodeResult.Delay>(result);
        Assert.Equal(5000, delay.DurationMs);
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"Should not block; elapsed {stopwatch.ElapsedMilliseconds} ms.");
    }
}
