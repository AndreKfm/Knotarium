using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Starts a run of the configured global error-handler workflow, carrying the failed run's context
/// payload and flattened failure globals, and queues it. The error-workflow spine (queue + worker)
/// lives in the Notifications slice; this seam lets that worker enqueue an error run without
/// depending on the Execution slice that owns the enqueuer implementation.
/// </summary>
public interface IErrorWorkflowRunEnqueuer
{
    /// <returns>The new error-run execution id, or <see langword="null"/> when the handler has no active version.</returns>
    Task<ExecutionInstanceId?> EnqueueAsync(
        WorkflowDefinitionId errorWorkflowId,
        ExecutionInstanceId sourceExecutionId,
        object? payload,
        IReadOnlyDictionary<string, object?>? extraGlobals = null,
        CancellationToken cancellationToken = default);
}
