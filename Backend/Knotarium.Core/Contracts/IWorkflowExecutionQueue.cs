using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Producer side of the in-memory hand-off from a run-starting service (schedule/poll/error/manual
/// enqueuers) to the workflow-execution worker that drains and runs pending executions. Only the
/// producer method is exposed here — the draining worker keeps a reference to the concrete queue —
/// so slices that merely start runs (Polling, Schedules) depend on this seam, not on Execution.
/// </summary>
public interface IWorkflowExecutionQueue
{
    /// <summary>Queues a persisted, Pending execution for the worker to pick up. Non-blocking.</summary>
    void QueueExecution(ExecutionInstanceId executionId);
}
