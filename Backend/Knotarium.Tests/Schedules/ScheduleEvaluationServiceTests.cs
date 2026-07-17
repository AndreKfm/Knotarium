// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Schedules;
using Knotarium.Infrastructure.Persistence;
using Xunit;

namespace Knotarium.Tests.Schedules;

public class ScheduleEvaluationServiceTests : IDisposable
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public ScheduleEvaluationServiceTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"schedule-evaluation-{Guid.NewGuid():N}.db");
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
    public async Task EvaluateActiveSchedulesAsync_DuplicateClaimAdvancesNextFireAndStopsReprocessingSameSlot()
    {
        var fixedNow = new DateTimeOffset(2026, 5, 31, 12, 2, 0, TimeSpan.Zero);
        var scheduleId = await SeedScheduleAsync(fixedNow.AddMinutes(-2), "*/5 * * * *", "UTC");
        var enqueueService = new RecordingWorkflowEnqueueService(_ => ScheduleEnqueueResult.DuplicateClaim);
        var timeProvider = new FixedTimeProvider(fixedNow);

        await using (var context = CreateContext())
        {
            var service = CreateService(context, enqueueService, timeProvider);
            await service.EvaluateActiveSchedulesAsync();
            await service.EvaluateActiveSchedulesAsync();
        }

        Assert.Single(enqueueService.Requests);
        Assert.Equal(fixedNow.AddMinutes(-2), enqueueService.Requests[0].PlannedFireAtUtc);
        Assert.Equal(fixedNow.AddMinutes(3), enqueueService.Requests[0].NextFireAtUtc);

        await using var verificationContext = CreateContext();
        var schedule = await verificationContext.Schedules.SingleAsync(item => item.Id == scheduleId);
        Assert.Equal(fixedNow.AddMinutes(3).ToUnixTimeMilliseconds(), schedule.NextFireAtUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task EvaluateActiveSchedulesAsync_BerlinDstTransition_ComputesNextOccurrenceWithShiftedOffset()
    {
        var plannedFireAtUtc = new DateTimeOffset(2026, 3, 28, 2, 30, 0, TimeSpan.Zero);
        var scheduleId = await SeedScheduleAsync(plannedFireAtUtc, "30 3 * * *", "Europe/Berlin");
        var enqueueService = new RecordingWorkflowEnqueueService(_ => ScheduleEnqueueResult.DuplicateClaim);
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero));

        await using (var context = CreateContext())
        {
            var service = CreateService(context, enqueueService, timeProvider);
            await service.EvaluateActiveSchedulesAsync();
        }

        Assert.Single(enqueueService.Requests);
        Assert.Equal(scheduleId, enqueueService.Requests[0].ScheduleId);
        Assert.Equal(plannedFireAtUtc, enqueueService.Requests[0].PlannedFireAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), enqueueService.Requests[0].NextFireAtUtc);
    }

    [Fact]
    public async Task EvaluateActiveSchedulesAsync_LongDowntime_CapsCatchUpTraversalAtTenWindows()
    {
        var plannedFireAtUtc = new DateTimeOffset(2026, 5, 28, 12, 0, 0, TimeSpan.Zero);
        await SeedScheduleAsync(plannedFireAtUtc, "* * * * *", "UTC");

        var enqueueService = new RecordingWorkflowEnqueueService(_ => ScheduleEnqueueResult.Enqueued);
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero));

        await using (var context = CreateContext())
        {
            var service = CreateService(context, enqueueService, timeProvider);
            await service.EvaluateActiveSchedulesAsync();
        }

        Assert.Single(enqueueService.Requests);
        Assert.Equal(plannedFireAtUtc.AddMinutes(10), enqueueService.Requests[0].PlannedFireAtUtc);
        Assert.Equal(plannedFireAtUtc.AddMinutes(11), enqueueService.Requests[0].NextFireAtUtc);
    }

    [Fact]
    public async Task EvaluateActiveSchedulesAsync_SecondsCron_AdvancesUsingSecondPrecision()
    {
        var plannedFireAtUtc = new DateTimeOffset(2026, 5, 31, 12, 0, 5, TimeSpan.Zero);
        var scheduleId = await SeedScheduleAsync(plannedFireAtUtc, "*/5 * * * * *", "UTC");

        var enqueueService = new RecordingWorkflowEnqueueService(_ => ScheduleEnqueueResult.Enqueued);
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 5, 31, 12, 0, 12, TimeSpan.Zero));

        await using (var context = CreateContext())
        {
            var service = CreateService(context, enqueueService, timeProvider);
            await service.EvaluateActiveSchedulesAsync();
        }

        Assert.Single(enqueueService.Requests);
        Assert.Equal(scheduleId, enqueueService.Requests[0].ScheduleId);
        Assert.Equal(new DateTimeOffset(2026, 5, 31, 12, 0, 10, TimeSpan.Zero), enqueueService.Requests[0].PlannedFireAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 5, 31, 12, 0, 15, TimeSpan.Zero), enqueueService.Requests[0].NextFireAtUtc);
    }

    [Fact]
    public async Task EvaluateActiveSchedulesAsync_DisabledWorkflow_DoesNotFire()
    {
        var fixedNow = new DateTimeOffset(2026, 5, 31, 12, 2, 0, TimeSpan.Zero);
        var workflowId = await SeedWorkflowAsync(isEnabled: false);
        await SeedScheduleAsync(fixedNow.AddMinutes(-2), "*/5 * * * *", "UTC", workflowId);
        var enqueueService = new RecordingWorkflowEnqueueService(_ => ScheduleEnqueueResult.Enqueued);
        var timeProvider = new FixedTimeProvider(fixedNow);

        await using (var context = CreateContext())
        {
            var service = CreateService(context, enqueueService, timeProvider);
            await service.EvaluateActiveSchedulesAsync();
        }

        Assert.Empty(enqueueService.Requests);
    }

    [Fact]
    public async Task EvaluateActiveSchedulesAsync_EnabledWorkflow_Fires()
    {
        var fixedNow = new DateTimeOffset(2026, 5, 31, 12, 2, 0, TimeSpan.Zero);
        var workflowId = await SeedWorkflowAsync(isEnabled: true);
        await SeedScheduleAsync(fixedNow.AddMinutes(-2), "*/5 * * * *", "UTC", workflowId);
        var enqueueService = new RecordingWorkflowEnqueueService(_ => ScheduleEnqueueResult.Enqueued);
        var timeProvider = new FixedTimeProvider(fixedNow);

        await using (var context = CreateContext())
        {
            var service = CreateService(context, enqueueService, timeProvider);
            await service.EvaluateActiveSchedulesAsync();
        }

        Assert.Single(enqueueService.Requests);
    }

    private ScheduleEvaluationService CreateService(
        AppDbContext context,
        IWorkflowEnqueueService enqueueService,
        TimeProvider timeProvider)
    {
        return new ScheduleEvaluationService(
            new DbScheduleStore(context),
            enqueueService,
            timeProvider,
            NullLogger<ScheduleEvaluationService>.Instance);
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

    private async Task<Guid> SeedScheduleAsync(
        DateTimeOffset nextFireAtUtc,
        string cronExpression,
        string timeZoneId,
        WorkflowDefinitionId? workflowDefinitionId = null)
    {
        var scheduleId = Guid.NewGuid();

        await using var context = CreateContext();
        context.Schedules.Add(new Schedule
        {
            Id = scheduleId,
            WorkflowDefinitionId = workflowDefinitionId ?? WorkflowDefinitionId.New(),
            CronExpression = cronExpression,
            TimeZoneId = timeZoneId,
            NextFireAtUtc = nextFireAtUtc,
            IsActive = true
        });

        await context.SaveChangesAsync();
        return scheduleId;
    }

    private async Task<WorkflowDefinitionId> SeedWorkflowAsync(bool isEnabled)
    {
        var workflowId = WorkflowDefinitionId.New();

        await using var context = CreateContext();
        context.WorkflowDefinitions.Add(new WorkflowDefinition(
            workflowId,
            "test-workflow",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>())
        {
            IsEnabled = isEnabled
        });

        await context.SaveChangesAsync();
        return workflowId;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class RecordingWorkflowEnqueueService : IWorkflowEnqueueService
    {
        private readonly Func<ClaimRequest, ScheduleEnqueueResult> _resultFactory;

        public RecordingWorkflowEnqueueService(Func<ClaimRequest, ScheduleEnqueueResult> resultFactory)
        {
            _resultFactory = resultFactory;
        }

        public List<ClaimRequest> Requests { get; } = new();

        public Task<ScheduleEnqueueResult> ClaimAndEnqueueScheduleAsync(
            Guid scheduleId,
            DateTimeOffset plannedFireAtUtc,
            DateTimeOffset nextFireAtUtc,
            CancellationToken cancellationToken = default)
        {
            var request = new ClaimRequest(scheduleId, plannedFireAtUtc, nextFireAtUtc);
            Requests.Add(request);
            return Task.FromResult(_resultFactory(request));
        }
    }

    private sealed record ClaimRequest(Guid ScheduleId, DateTimeOffset PlannedFireAtUtc, DateTimeOffset NextFireAtUtc);
}