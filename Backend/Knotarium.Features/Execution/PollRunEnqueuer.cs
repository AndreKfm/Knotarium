using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Features.Execution;

/// <summary>
/// Default <see cref="IPollRunEnqueuer"/>: creates an ExecutionInstance carrying the polled payload and
/// queues it. Lives in the Execution slice (the sanctioned AppDbContext owner) alongside the sibling
/// run enqueuers (<see cref="ErrorWorkflowRunEnqueuer"/>, <see cref="ExternalSignalRunEnqueuer"/>); the
/// Polling slice consumes it only through the <see cref="IPollRunEnqueuer"/> Core seam.
/// </summary>
public sealed class PollRunEnqueuer : IPollRunEnqueuer
{
    public const string PayloadVariableKey = TriggerPayloadKeys.Poll;

    private readonly AppDbContext _dbContext;
    private readonly IWorkflowExecutionQueue _queue;
    private readonly ActiveWorkflowVersionService _activeWorkflowVersionService;
    private readonly TimeProvider _timeProvider;

    public PollRunEnqueuer(
        AppDbContext dbContext,
        IWorkflowExecutionQueue queue,
        ActiveWorkflowVersionService activeWorkflowVersionService,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _activeWorkflowVersionService = activeWorkflowVersionService ?? throw new ArgumentNullException(nameof(activeWorkflowVersionService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<bool> EnqueueAsync(WorkflowDefinitionId workflowId, object? payload, CancellationToken cancellationToken)
    {
        var version = await _activeWorkflowVersionService.GetActiveVersionAsync(workflowId, cancellationToken);
        if (version is null)
        {
            return false;
        }

        var globals = new Dictionary<string, object>();
        if (payload is not null)
        {
            globals[PayloadVariableKey] = payload;
        }

        var execution = new ExecutionInstance
        {
            Id = ExecutionInstanceId.New(),
            WorkflowDefinitionId = workflowId,
            WorkflowVersionId = version.Id,
            Status = ExecutionStatus.Pending,
            CreatedAt = _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow(),
            TriggerOrigin = "poll",
            GlobalVariables = globals
        };

        // Persist inside a transaction and only push to the in-memory queue after commit,
        // so a crash between the DB write and the enqueue can't leave a Pending execution
        // that is never picked up (mirrors WorkflowEnqueueService).
        await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            await _dbContext.ExecutionInstances.AddAsync(execution, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        _queue.QueueExecution(execution.Id);
        return true;
    }
}
