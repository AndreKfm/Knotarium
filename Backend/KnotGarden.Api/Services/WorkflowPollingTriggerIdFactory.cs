using System;
using System.Security.Cryptography;
using System.Text;
using KnotGarden.Core.Domain;

namespace KnotGarden.Api.Services;

/// <summary>Creates stable polling-trigger identifiers for pollingTrigger nodes within a workflow.</summary>
internal static class WorkflowPollingTriggerIdFactory
{
    public static Guid Create(WorkflowDefinitionId workflowId, NodeId nodeId)
    {
        // Distinct namespace prefix from schedules so a scheduler and a pollingTrigger sharing a node id never collide.
        var keyBytes = Encoding.UTF8.GetBytes($"poll:{workflowId.Value}:{nodeId.Value}");
        var hash = MD5.HashData(keyBytes);
        return new Guid(hash);
    }
}
