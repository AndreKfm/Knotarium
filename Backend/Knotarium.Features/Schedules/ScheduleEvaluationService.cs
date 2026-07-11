using System;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Schedules;

/// <summary>
/// Evaluates due schedules using timezone-aware cron occurrences and advances them through the enqueue boundary.
/// </summary>
public sealed partial class ScheduleEvaluationService : IScheduleEvaluationService
{
    private const int MaxCatchUpWindows = 10;

    private readonly IScheduleStore _scheduleStore;
    private readonly IWorkflowEnqueueService _workflowEnqueueService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScheduleEvaluationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduleEvaluationService"/> class.
    /// </summary>
    /// <param name="scheduleStore">The schedule read/advance seam.</param>
    /// <param name="workflowEnqueueService">The schedule fire enqueue boundary.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public ScheduleEvaluationService(
        IScheduleStore scheduleStore,
        IWorkflowEnqueueService workflowEnqueueService,
        TimeProvider timeProvider,
        ILogger<ScheduleEvaluationService> logger)
    {
        _scheduleStore = scheduleStore ?? throw new ArgumentNullException(nameof(scheduleStore));
        _workflowEnqueueService = workflowEnqueueService ?? throw new ArgumentNullException(nameof(workflowEnqueueService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task EvaluateActiveSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        // Skip schedules whose owning workflow is explicitly deactivated. A workflow without a
        // database header yet (created but never published) is not excluded here — it simply has
        // no active version to enqueue. Manual runs bypass this path entirely.
        var dueSchedules = await _scheduleStore.GetDueAsync(now, cancellationToken);

        foreach (var schedule in dueSchedules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ProcessScheduleAsync(schedule, now, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Log.ScheduleEvaluationFailed(_logger, schedule.Id, exception);
            }
        }
    }

    private async Task ProcessScheduleAsync(Schedule schedule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        var cronExpression = CronExpressionParser.Parse(schedule.CronExpression);
        var occurrenceWindow = ComputeOccurrenceWindow(cronExpression, timeZone, schedule.NextFireAtUtc, now);

        if (!occurrenceWindow.HasValue)
        {
            Log.ScheduleHasNoFutureOccurrence(_logger, schedule.Id, schedule.CronExpression, schedule.TimeZoneId);
            return;
        }

        var (plannedFireAtUtc, nextFireAtUtc, catchUpCount) = occurrenceWindow.Value;
        var enqueueResult = await _workflowEnqueueService.ClaimAndEnqueueScheduleAsync(
            schedule.Id,
            plannedFireAtUtc,
            nextFireAtUtc,
            cancellationToken);

        if (catchUpCount == MaxCatchUpWindows && nextFireAtUtc <= now)
        {
            Log.CatchUpCapReached(_logger, schedule.Id, plannedFireAtUtc, nextFireAtUtc);
        }

        if (enqueueResult == ScheduleEnqueueResult.Enqueued)
        {
            return;
        }

        if (enqueueResult == ScheduleEnqueueResult.DuplicateClaim)
        {
            await _scheduleStore.AdvanceNextFireAsync(schedule, nextFireAtUtc, cancellationToken);

            Log.DuplicateScheduleFireAdvanced(_logger, schedule.Id, plannedFireAtUtc, nextFireAtUtc);
            return;
        }

        Log.MissingActiveVersionSkipped(_logger, schedule.Id, plannedFireAtUtc, nextFireAtUtc);
    }

    private static (DateTimeOffset PlannedFireAtUtc, DateTimeOffset NextFireAtUtc, int CatchUpCount)? ComputeOccurrenceWindow(
        CronExpression cronExpression,
        TimeZoneInfo timeZone,
        DateTimeOffset initialPlannedFireAtUtc,
        DateTimeOffset now)
    {
        var plannedFireAtUtc = initialPlannedFireAtUtc;
        var nextFireAtUtc = GetNextOccurrenceUtc(cronExpression, timeZone, plannedFireAtUtc);
        if (!nextFireAtUtc.HasValue)
        {
            return null;
        }

        var catchUpCount = 0;
        while (nextFireAtUtc.Value <= now && catchUpCount < MaxCatchUpWindows)
        {
            plannedFireAtUtc = nextFireAtUtc.Value;
            nextFireAtUtc = GetNextOccurrenceUtc(cronExpression, timeZone, plannedFireAtUtc);
            if (!nextFireAtUtc.HasValue)
            {
                return null;
            }

            catchUpCount++;
        }

        return (plannedFireAtUtc, nextFireAtUtc.Value, catchUpCount);
    }

    private static DateTimeOffset? GetNextOccurrenceUtc(
        CronExpression cronExpression,
        TimeZoneInfo timeZone,
        DateTimeOffset currentOccurrenceUtc)
    {
        return cronExpression.GetNextOccurrence(currentOccurrenceUtc, timeZone);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1100, Level = LogLevel.Error, Message = "Failed to evaluate schedule {ScheduleId}.")]
        public static partial void ScheduleEvaluationFailed(ILogger logger, Guid scheduleId, Exception exception);

        [LoggerMessage(EventId = 1101, Level = LogLevel.Warning, Message = "Schedule {ScheduleId} with cron '{CronExpression}' in time zone '{TimeZoneId}' has no future occurrence.")]
        public static partial void ScheduleHasNoFutureOccurrence(ILogger logger, Guid scheduleId, string cronExpression, string timeZoneId);

        [LoggerMessage(EventId = 1102, Level = LogLevel.Warning, Message = "Schedule {ScheduleId} hit the catch-up cap while advancing from {PlannedFireAtUtc} to {NextFireAtUtc}.")]
        public static partial void CatchUpCapReached(ILogger logger, Guid scheduleId, DateTimeOffset plannedFireAtUtc, DateTimeOffset nextFireAtUtc);

        [LoggerMessage(EventId = 1103, Level = LogLevel.Warning, Message = "Duplicate schedule fire was rejected for schedule {ScheduleId} at {PlannedFireAtUtc}; advanced next fire to {NextFireAtUtc}.")]
        public static partial void DuplicateScheduleFireAdvanced(ILogger logger, Guid scheduleId, DateTimeOffset plannedFireAtUtc, DateTimeOffset nextFireAtUtc);

        [LoggerMessage(EventId = 1104, Level = LogLevel.Warning, Message = "Skipped schedule {ScheduleId} fire at {PlannedFireAtUtc} because no active workflow version exists; next fire is {NextFireAtUtc}.")]
        public static partial void MissingActiveVersionSkipped(ILogger logger, Guid scheduleId, DateTimeOffset plannedFireAtUtc, DateTimeOffset nextFireAtUtc);
    }
}