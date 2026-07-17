// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Knotarium.Core.Domain;
using Knotarium.Features.Execution;
using Xunit;

namespace Knotarium.Tests.Execution;

public class ExecutionOptionsAndQueueTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void FromConfiguration_MissingSection_UsesConservativeDefaults()
    {
        var options = ExecutionOptions.FromConfiguration(Config(new Dictionary<string, string?>()));

        Assert.Equal(4, options.MaxConcurrentRuns);
        Assert.True(options.JournalBatchingEnabled);
        Assert.Equal(32, options.JournalBatchMaxSize);
    }

    [Theory]
    [InlineData(0, 1)]     // below floor → clamped up to the serial kill-switch
    [InlineData(-5, 1)]
    [InlineData(1, 1)]     // the explicit serial value survives
    [InlineData(8, 8)]
    [InlineData(999, 64)]  // above ceiling → clamped to the parallelForEach precedent
    public void FromConfiguration_ClampsMaxConcurrentRuns(int configured, int expected)
    {
        var options = ExecutionOptions.FromConfiguration(Config(new Dictionary<string, string?>
        {
            ["Execution:MaxConcurrentRuns"] = configured.ToString(),
        }));

        Assert.Equal(expected, options.MaxConcurrentRuns);
    }

    [Fact]
    public void FromConfiguration_BindsAndClampsBatchingKnobs()
    {
        var options = ExecutionOptions.FromConfiguration(Config(new Dictionary<string, string?>
        {
            ["Execution:JournalBatchingEnabled"] = "false",
            ["Execution:JournalBatchMaxSize"] = "5000",           // above ceiling
            ["Execution:JournalBatchMaxDelayMilliseconds"] = "0", // below floor
        }));

        Assert.False(options.JournalBatchingEnabled);
        Assert.Equal(256, options.JournalBatchMaxSize);
        Assert.Equal(1, options.JournalBatchMaxDelayMilliseconds);
    }

    [Fact]
    public void Queue_TracksDepthAcrossEnqueueAndDequeue()
    {
        var queue = new WorkflowExecutionQueue(new ExecutionOptions { MaxQueueDepth = 10 });
        Assert.Equal(0, queue.Depth);

        queue.QueueExecution(ExecutionInstanceId.New());
        queue.QueueExecution(ExecutionInstanceId.New());
        Assert.Equal(2, queue.Depth);

        Assert.True(queue.TryDequeue(out _));
        Assert.Equal(1, queue.Depth);
    }

    [Fact]
    public void TryQueueExecution_RejectsOnceDepthCapReached()
    {
        var queue = new WorkflowExecutionQueue(new ExecutionOptions { MaxQueueDepth = 2 });

        Assert.True(queue.TryQueueExecution(ExecutionInstanceId.New()));
        Assert.True(queue.TryQueueExecution(ExecutionInstanceId.New()));
        Assert.True(queue.IsFull);

        // Rejectable path refuses the third; depth is unchanged.
        Assert.False(queue.TryQueueExecution(ExecutionInstanceId.New()));
        Assert.Equal(2, queue.Depth);

        // Internal producers bypass the cap (their runs are already persisted Pending).
        queue.QueueExecution(ExecutionInstanceId.New());
        Assert.Equal(3, queue.Depth);
    }

    [Fact]
    public void RuntimeMonitor_TracksInFlightAndRejections()
    {
        var monitor = new ExecutionRuntimeMonitor();
        Assert.Equal(0, monitor.InFlightRuns);

        monitor.RunStarted();
        monitor.RunStarted();
        Assert.Equal(2, monitor.InFlightRuns);

        monitor.RunFinished();
        Assert.Equal(1, monitor.InFlightRuns);

        monitor.StartRejected();
        monitor.StartRejected();
        Assert.Equal(2, monitor.RejectedStarts);
    }
}
