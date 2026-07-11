using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Infrastructure.Persistence;

namespace KnotGarden.Features.Execution;

/// <summary>
/// Persists schedule fire claims and creates workflow executions with a fire-level idempotent database
/// boundary. Lives in the Execution slice (the sanctioned AppDbContext owner, and where run creation
/// belongs); the Schedules slice consumes it only through the <see cref="IWorkflowEnqueueService"/> Core
/// seam. The <c>ScheduleFire</c> claim and the <c>ExecutionInstance</c> creation must stay in one
/// transaction, so this can't delegate run creation to a generic submission seam.
/// </summary>
public class WorkflowEnqueueService : IWorkflowEnqueueService
{
    private const int SqliteConstraintErrorCode = 19;
    private const int SqliteUniqueConstraintExtendedCode = 2067;

    private readonly AppDbContext _dbContext;
    private readonly IWorkflowExecutionQueue _queue;
    private readonly ActiveWorkflowVersionService _activeWorkflowVersionService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowEnqueueService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkflowEnqueueService"/> class.
    /// </summary>
    public WorkflowEnqueueService(
        AppDbContext dbContext,
        IWorkflowExecutionQueue queue,
        ActiveWorkflowVersionService activeWorkflowVersionService,
        TimeProvider timeProvider,
        ILogger<WorkflowEnqueueService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _activeWorkflowVersionService = activeWorkflowVersionService ?? throw new ArgumentNullException(nameof(activeWorkflowVersionService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ScheduleEnqueueResult> ClaimAndEnqueueScheduleAsync(
        Guid scheduleId,
        DateTimeOffset plannedFireAtUtc,
        DateTimeOffset nextFireAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (scheduleId == Guid.Empty)
        {
            throw new ArgumentException("Schedule id must be non-empty.", nameof(scheduleId));
        }

        if (nextFireAtUtc < plannedFireAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(nextFireAtUtc), "Next fire time must not move backwards.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var schedule = await _dbContext.Schedules.SingleOrDefaultAsync(s => s.Id == scheduleId, cancellationToken)
                ?? throw new InvalidOperationException($"Schedule '{scheduleId}' was not found.");

            var fire = new ScheduleFire
            {
                Id = Guid.NewGuid(),
                ScheduleId = scheduleId,
                PlannedFireAtUtc = plannedFireAtUtc,
                FiredAtUtc = _timeProvider.GetUtcNow(),
                Status = ScheduleFireStatus.Claimed
            };

            await _dbContext.ScheduleFires.AddAsync(fire, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var workflow = await _dbContext.WorkflowDefinitions.SingleOrDefaultAsync(w => w.Id == schedule.WorkflowDefinitionId, cancellationToken)
                ?? throw new InvalidOperationException($"Workflow definition '{schedule.WorkflowDefinitionId.Value}' was not found for schedule '{scheduleId}'.");

            var executionVersion = await _activeWorkflowVersionService.GetActiveVersionAsync(schedule.WorkflowDefinitionId, cancellationToken);
            if (executionVersion is null)
            {
                fire.Status = ScheduleFireStatus.Failed;
                schedule.NextFireAtUtc = nextFireAtUtc;

                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                _logger.LogWarning(
                    "Skipped schedule fire for schedule {ScheduleId} at {PlannedFireAtUtc} because workflow {WorkflowDefinitionId} has no active version.",
                    scheduleId,
                    plannedFireAtUtc,
                    schedule.WorkflowDefinitionId.Value);
                return ScheduleEnqueueResult.NoActiveVersion;
            }

            var execution = new ExecutionInstance
            {
                Id = ExecutionInstanceId.New(),
                WorkflowDefinitionId = workflow.Id,
                WorkflowVersionId = executionVersion.Id,
                Status = ExecutionStatus.Pending,
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow(),
                TriggerOrigin = "schedule",
                GlobalVariables = new Dictionary<string, object>()
            };

            await _dbContext.ExecutionInstances.AddAsync(execution, cancellationToken);

            fire.ExecutionInstanceId = execution.Id;
            fire.Status = ScheduleFireStatus.ExecutionCreated;
            schedule.NextFireAtUtc = nextFireAtUtc;

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _queue.QueueExecution(execution.Id);
            return ScheduleEnqueueResult.Enqueued;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogWarning(
                "Duplicate schedule fire rejected for schedule {ScheduleId} at {PlannedFireAtUtc}.",
                scheduleId,
                plannedFireAtUtc);
            return ScheduleEnqueueResult.DuplicateClaim;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is SqliteException sqliteException
            && sqliteException.SqliteErrorCode == SqliteConstraintErrorCode
            && (sqliteException.SqliteExtendedErrorCode == SqliteUniqueConstraintExtendedCode
                || sqliteException.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal));
    }
}