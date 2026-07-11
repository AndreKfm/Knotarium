using System.Security.Cryptography;
using System.Text;
using KnotGarden.Core.Domain;

namespace KnotGarden.Api.Services;

/// <summary>
/// Creates stable schedule identifiers for scheduler nodes within a workflow.
/// </summary>
internal static class WorkflowScheduleIdFactory
{
    /// <summary>
    /// Creates a deterministic schedule identifier for a workflow scheduler node.
    /// </summary>
    /// <param name="workflowId">The owning workflow identifier.</param>
    /// <param name="nodeId">The scheduler node identifier.</param>
    /// <returns>The deterministic schedule identifier.</returns>
    public static Guid Create(WorkflowDefinitionId workflowId, NodeId nodeId)
    {
        var keyBytes = Encoding.UTF8.GetBytes($"{workflowId.Value}:{nodeId.Value}");
        var hash = MD5.HashData(keyBytes);
        return new Guid(hash);
    }
}