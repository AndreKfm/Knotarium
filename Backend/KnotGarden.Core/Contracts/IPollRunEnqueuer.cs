using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Domain;

namespace KnotGarden.Core.Contracts;

/// <summary>Creates and queues a workflow run started by a polling trigger.</summary>
public interface IPollRunEnqueuer
{
    /// <summary>Returns true if a run was created (false when the workflow has no active version).</summary>
    Task<bool> EnqueueAsync(WorkflowDefinitionId workflowId, object? payload, CancellationToken cancellationToken);
}
