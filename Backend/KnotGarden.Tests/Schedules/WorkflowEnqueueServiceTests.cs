using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Execution;
using KnotGarden.Features.Schedules;
using KnotGarden.Infrastructure.Persistence;
using Xunit;

namespace KnotGarden.Tests.Schedules;

public class WorkflowEnqueueServiceTests : IDisposable
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public WorkflowEnqueueServiceTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"workflow-enqueue-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_databasePath}";

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new AppDbContext(options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task ClaimAndEnqueueScheduleAsync_ConcurrentDuplicateClaims_OnlyOneExecutionIsCreated()
    {
        var (scheduleId, plannedFireAtUtc, nextFireAtUtc) = await SeedScheduleAsync();

        const int taskCount = 8;
        var tasks = Enumerable.Range(0, taskCount)
            .Select(_ => Task.Run(async () =>
            {
                await using var context = CreateContext();
                var queue = new WorkflowExecutionQueue();
                var activeWorkflowVersionService = new ActiveWorkflowVersionService(context, TimeProvider.System);
                var service = new WorkflowEnqueueService(
                    context,
                    queue,
                    activeWorkflowVersionService,
                    TimeProvider.System,
                    NullLogger<WorkflowEnqueueService>.Instance);

                return await service.ClaimAndEnqueueScheduleAsync(scheduleId, plannedFireAtUtc, nextFireAtUtc);
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => result == ScheduleEnqueueResult.Enqueued);
        Assert.Equal(taskCount - 1, results.Count(result => result == ScheduleEnqueueResult.DuplicateClaim));

        await using var verificationContext = CreateContext();
        var fires = await verificationContext.ScheduleFires.Where(fire => fire.ScheduleId == scheduleId).ToListAsync();
        var executions = await verificationContext.ExecutionInstances.ToListAsync();
        var schedule = await verificationContext.Schedules.SingleAsync(item => item.Id == scheduleId);

        Assert.Single(fires);
        Assert.Single(executions);
        Assert.Equal(ScheduleFireStatus.ExecutionCreated, fires[0].Status);
        Assert.Equal(executions[0].Id, fires[0].ExecutionInstanceId);
        Assert.Equal(nextFireAtUtc.ToUnixTimeMilliseconds(), schedule.NextFireAtUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ClaimAndEnqueueScheduleAsync_Success_TransitionsScheduleFireToExecutionCreated()
    {
        var (scheduleId, plannedFireAtUtc, nextFireAtUtc) = await SeedScheduleAsync();

        await using var context = CreateContext();
        var queue = new WorkflowExecutionQueue();
        var activeWorkflowVersionService = new ActiveWorkflowVersionService(context, TimeProvider.System);
        var service = new WorkflowEnqueueService(
            context,
            queue,
            activeWorkflowVersionService,
            TimeProvider.System,
            NullLogger<WorkflowEnqueueService>.Instance);

        var claimed = await service.ClaimAndEnqueueScheduleAsync(scheduleId, plannedFireAtUtc, nextFireAtUtc);

        Assert.Equal(ScheduleEnqueueResult.Enqueued, claimed);
        Assert.True(queue.TryDequeue(out var queuedExecutionId));

        await using var verificationContext = CreateContext();
        var fire = await verificationContext.ScheduleFires.SingleAsync(item => item.ScheduleId == scheduleId);
        var execution = await verificationContext.ExecutionInstances.SingleAsync(item => item.Id == fire.ExecutionInstanceId);
        var schedule = await verificationContext.Schedules.SingleAsync(item => item.Id == scheduleId);

        Assert.Equal(ScheduleFireStatus.ExecutionCreated, fire.Status);
        Assert.Equal(queuedExecutionId, execution.Id);
        Assert.Equal(ExecutionStatus.Pending, execution.Status);
        Assert.Equal(nextFireAtUtc.ToUnixTimeMilliseconds(), schedule.NextFireAtUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ClaimAndEnqueueScheduleAsync_MissingWorkflowDefinition_RethrowsFailure()
    {
        var scheduleId = Guid.NewGuid();
        var workflowId = WorkflowDefinitionId.New();
        var plannedFireAtUtc = DateTimeOffset.UtcNow;
        var nextFireAtUtc = plannedFireAtUtc.AddMinutes(5);

        await using (var context = CreateContext())
        {
            context.Schedules.Add(new Schedule
            {
                Id = scheduleId,
                WorkflowDefinitionId = workflowId,
                CronExpression = "*/5 * * * *",
                TimeZoneId = "UTC",
                NextFireAtUtc = plannedFireAtUtc,
                IsActive = true
            });
            await context.SaveChangesAsync();
        }

        await using var failingContext = CreateContext();
        var activeWorkflowVersionService = new ActiveWorkflowVersionService(failingContext, TimeProvider.System);
        var service = new WorkflowEnqueueService(
            failingContext,
            new WorkflowExecutionQueue(),
            activeWorkflowVersionService,
            TimeProvider.System,
            NullLogger<WorkflowEnqueueService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ClaimAndEnqueueScheduleAsync(scheduleId, plannedFireAtUtc, nextFireAtUtc));

        await using var verificationContext = CreateContext();
        Assert.Empty(await verificationContext.ScheduleFires.ToListAsync());
        Assert.Empty(await verificationContext.ExecutionInstances.ToListAsync());
    }

    [Fact]
    public async Task ClaimAndEnqueueScheduleAsync_NoActiveVersion_SkipsExecutionAndAdvancesSchedule()
    {
        var (scheduleId, plannedFireAtUtc, nextFireAtUtc) = await SeedScheduleAsync(createActiveVersion: false);

        await using var context = CreateContext();
        var queue = new WorkflowExecutionQueue();
        var activeWorkflowVersionService = new ActiveWorkflowVersionService(context, TimeProvider.System);
        var service = new WorkflowEnqueueService(
            context,
            queue,
            activeWorkflowVersionService,
            TimeProvider.System,
            NullLogger<WorkflowEnqueueService>.Instance);

        var result = await service.ClaimAndEnqueueScheduleAsync(scheduleId, plannedFireAtUtc, nextFireAtUtc);

        Assert.Equal(ScheduleEnqueueResult.NoActiveVersion, result);
        Assert.False(queue.TryDequeue(out _));

        await using var verificationContext = CreateContext();
        var fire = await verificationContext.ScheduleFires.SingleAsync(item => item.ScheduleId == scheduleId);
        var schedule = await verificationContext.Schedules.SingleAsync(item => item.Id == scheduleId);

        Assert.Equal(ScheduleFireStatus.Failed, fire.Status);
        Assert.Equal(nextFireAtUtc.ToUnixTimeMilliseconds(), schedule.NextFireAtUtc.ToUnixTimeMilliseconds());
        Assert.Empty(await verificationContext.ExecutionInstances.ToListAsync());
    }

    private AppDbContext CreateContext()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        return new AppDbContext(options);
    }

    private async Task<(Guid ScheduleId, DateTimeOffset PlannedFireAtUtc, DateTimeOffset NextFireAtUtc)> SeedScheduleAsync(bool createActiveVersion = true)
    {
        var workflowId = WorkflowDefinitionId.New();
        var scheduleId = Guid.NewGuid();
        var plannedFireAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var nextFireAtUtc = plannedFireAtUtc.AddMinutes(5);
        var nodes = new[]
        {
            new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>()),
            new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>())
        };
        var edges = new[]
        {
            new EdgeDefinition("e1", NodeId.Create("start"), "result", NodeId.Create("end"), "in")
        };
        var workflowVersionId = WorkflowVersionId.New();

        await using var context = CreateContext();
        context.WorkflowDefinitions.Add(new WorkflowDefinition(
            workflowId,
            "Scheduled Workflow",
            nodes,
            edges));

        context.WorkflowVersions.Add(new WorkflowVersion(
            workflowVersionId,
            workflowId,
            1,
            nodes,
            edges,
            DateTimeOffset.UtcNow));

        if (createActiveVersion)
        {
            context.ActiveWorkflowVersions.Add(new ActiveWorkflowVersion
            {
                WorkflowDefinitionId = workflowId,
                WorkflowVersionId = workflowVersionId,
                ActivatedAtUtc = DateTimeOffset.UtcNow
            });
        }

        context.Schedules.Add(new Schedule
        {
            Id = scheduleId,
            WorkflowDefinitionId = workflowId,
            CronExpression = "*/5 * * * *",
            TimeZoneId = "UTC",
            NextFireAtUtc = plannedFireAtUtc,
            IsActive = true
        });

        await context.SaveChangesAsync();
        return (scheduleId, plannedFireAtUtc, nextFireAtUtc);
    }
}