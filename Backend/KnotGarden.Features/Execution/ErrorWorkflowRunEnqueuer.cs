using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Infrastructure.Persistence;

namespace KnotGarden.Features.Execution;

/// <summary>
/// Creates an <see cref="ExecutionInstance"/> for the global error-handler workflow, carrying the
/// failed run's context payload, and queues it. Mirrors <c>PollRunEnqueuer</c>: transaction-then-queue
/// so a crash between the DB write and the enqueue can't strand a Pending execution. Returns false
/// (a no-op) when the target workflow has no active/published version.
/// </summary>
public sealed class ErrorWorkflowRunEnqueuer : IErrorWorkflowRunEnqueuer
{
    public const string PayloadVariableKey = TriggerPayloadKeys.Error;

    /// <summary>
    /// The flattened failure-context global keys (kept in sync with <c>ErrorWorkflowWorker.BuildGlobals</c>).
    /// The executor also emits each of these on the <c>errorTrigger</c> node's outputs so they can be
    /// promoted to draggable variables in the editor.
    /// </summary>
    public static readonly string[] FieldKeys =
    {
        "errorWorkflowId", "errorWorkflowName", "errorExecutionId", "errorFailedNodeId",
        "errorFailedNodeType", "errorMessage", "errorTriggerOrigin", "errorTimestampUtc"
    };

    private readonly AppDbContext _dbContext;
    private readonly WorkflowExecutionQueue _queue;
    private readonly ActiveWorkflowVersionService _activeWorkflowVersionService;
    private readonly TimeProvider _timeProvider;

    public ErrorWorkflowRunEnqueuer(
        AppDbContext dbContext,
        WorkflowExecutionQueue queue,
        ActiveWorkflowVersionService activeWorkflowVersionService,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _activeWorkflowVersionService = activeWorkflowVersionService ?? throw new ArgumentNullException(nameof(activeWorkflowVersionService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <returns>The new error-run execution id, or null when the handler has no active version.</returns>
    public async Task<ExecutionInstanceId?> EnqueueAsync(
        WorkflowDefinitionId errorWorkflowId,
        ExecutionInstanceId sourceExecutionId,
        object? payload,
        IReadOnlyDictionary<string, object?>? extraGlobals = null,
        CancellationToken cancellationToken = default)
    {
        var version = await _activeWorkflowVersionService.GetActiveVersionAsync(errorWorkflowId, cancellationToken);
        if (version is null)
        {
            return null;
        }

        var globals = new Dictionary<string, object>();
        if (payload is not null)
        {
            globals[PayloadVariableKey] = payload;
        }

        // Flattened failure fields (errorMessage, errorFailedNodeType, …) become first-class globals
        // so an error workflow can reference them directly in Log/Inline Code without unpacking the bundle.
        if (extraGlobals is not null)
        {
            foreach (var (key, value) in extraGlobals)
            {
                if (value is not null)
                {
                    globals[key] = value;
                }
            }
        }

        var execution = new ExecutionInstance
        {
            Id = ExecutionInstanceId.New(),
            WorkflowDefinitionId = errorWorkflowId,
            WorkflowVersionId = version.Id,
            Status = ExecutionStatus.Pending,
            CreatedAt = _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow(),
            TriggerOrigin = "error",
            ErrorOfExecutionId = sourceExecutionId,
            GlobalVariables = globals
        };

        // Breadcrumb on the error run linking back to the failed run it is handling.
        var backLink = new ExecutionJournal
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = execution.Id,
            NodeId = null,
            Timestamp = _timeProvider.GetUtcNow(),
            EventType = "ErrorWorkflowLink",
            Message = $"Handling failure of run {sourceExecutionId.Value}.",
            Data = new Dictionary<string, object> { ["sourceExecutionId"] = sourceExecutionId.Value.ToString() }
        };

        await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            await _dbContext.ExecutionInstances.AddAsync(execution, cancellationToken);
            await _dbContext.JournalEntries.AddAsync(backLink, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        _queue.QueueExecution(execution.Id);
        return execution.Id;
    }
}
