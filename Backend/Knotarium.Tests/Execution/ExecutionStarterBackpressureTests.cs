using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Knotarium.Features.Execution;
using Knotarium.Infrastructure.Persistence;
using Xunit;

namespace Knotarium.Tests.Execution;

public class ExecutionStarterBackpressureTests
{
    private static WorkflowDefinition TrivialWorkflow()
    {
        var start = new NodeDefinition(NodeId.Create("s"), "start", new System.Collections.Generic.Dictionary<string, object>());
        var end = new NodeDefinition(NodeId.Create("e"), "end", new System.Collections.Generic.Dictionary<string, object>());
        return new WorkflowDefinition(
            WorkflowDefinitionId.New(),
            "Backpressure Probe",
            new[] { start, end },
            new[] { new EdgeDefinition("x", start.Id, "result", end.Id, "in") });
    }

    [Fact]
    public async Task StartAsync_WhenQueueAtCapacity_ReturnsQueueFull_AndPersistsNoRun()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var queue = new WorkflowExecutionQueue(new ExecutionOptions { MaxQueueDepth = 1 });
        queue.QueueExecution(ExecutionInstanceId.New()); // saturate the queue
        Assert.True(queue.IsFull);

        var monitor = new ExecutionRuntimeMonitor();
        var compiler = new WorkflowCompiler(new SqliteWorkflowDefinitionProvider(context), new InMemoryNodePackageManifestProvider());
        var starter = new ExecutionStarter(context, compiler, queue, monitor);

        var outcome = await starter.StartAsync(TrivialWorkflow(), WorkflowVersionId.New(), "manual");

        Assert.True(outcome.IsQueueFull);
        Assert.False(outcome.IsStarted);
        Assert.Equal(1, outcome.QueueDepthLimit);
        Assert.Equal(1, monitor.RejectedStarts);

        // The rejection happens before compile/persist: no Pending run is left behind.
        Assert.Equal(0, await context.ExecutionInstances.CountAsync());
    }

    [Fact]
    public async Task StartAsync_WhenQueueHasRoom_StartsAndQueuesRun()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var queue = new WorkflowExecutionQueue(new ExecutionOptions { MaxQueueDepth = 10 });
        var monitor = new ExecutionRuntimeMonitor();
        var compiler = new WorkflowCompiler(new SqliteWorkflowDefinitionProvider(context), new InMemoryNodePackageManifestProvider());
        var starter = new ExecutionStarter(context, compiler, queue, monitor);

        var outcome = await starter.StartAsync(TrivialWorkflow(), WorkflowVersionId.New(), "manual");

        Assert.True(outcome.IsStarted);
        Assert.False(outcome.IsQueueFull);
        Assert.Equal(1, queue.Depth);
        Assert.Equal(0, monitor.RejectedStarts);
        Assert.Equal(1, await context.ExecutionInstances.CountAsync());
    }
}
