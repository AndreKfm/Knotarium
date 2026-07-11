using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;

namespace KnotGarden.Features.Notifications;

/// <summary>
/// Resolves the failure context of a failed run — the failed node id and a best-effort error message
/// — into a <see cref="FailureAlertMessage"/>. Shared by <see cref="FailureAlertWorker"/> (failure
/// alerts) and the error-workflow worker so the lookup lives in exactly one place.
///
/// The failed <see cref="NodeState"/> may not be committed yet when this runs: the consumers are
/// enqueued from the <c>WorkflowFailed</c> chokepoint, which fires before the executor's
/// <c>SaveChangesAsync</c>. Journal entries, however, are written through their own writer and are
/// durable by then — so when the NodeState isn't visible we recover the failed node id and message
/// from the latest <c>NodeExecutionFailed</c> journal entry, and only then fall back to the
/// <c>WorkflowFailed</c> message.
/// </summary>
public static class FailureContextBuilder
{
    /// <param name="instance">A failed execution with its <see cref="ExecutionInstance.NodeStates"/> loaded.</param>
    /// <param name="workflow">The workflow definition, if resolvable (used only for the display name).</param>
    public static async Task<FailureAlertMessage> BuildAsync(
        IExecutionReadStore readStore,
        ExecutionInstance instance,
        WorkflowDefinition? workflow,
        CancellationToken cancellationToken)
    {
        var failedNode = instance.NodeStates.LastOrDefault(ns => ns.Status == NodeStatus.Failed);
        var failedNodeId = failedNode?.NodeId.Value;
        var error = failedNode?.ErrorMessage;

        if (failedNodeId is null || string.IsNullOrWhiteSpace(error))
        {
            var failedEntry = await readStore.GetLatestJournalEntryAsync(
                instance.Id, JournalEventTypes.NodeExecutionFailed, cancellationToken);

            if (failedEntry is not null)
            {
                failedNodeId ??= failedEntry.NodeId?.Value;
                if (string.IsNullOrWhiteSpace(error))
                {
                    error = ExtractError(failedEntry);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(error))
        {
            var failedRunEntry = await readStore.GetLatestJournalEntryAsync(
                instance.Id, JournalEventTypes.WorkflowFailed, cancellationToken);
            error = failedRunEntry?.Message ?? "Workflow execution failed.";
        }

        return new FailureAlertMessage(
            WorkflowName: workflow?.Name ?? instance.WorkflowDefinitionId.Value,
            WorkflowId: instance.WorkflowDefinitionId.Value,
            ExecutionId: instance.Id.Value.ToString(),
            FailedNodeId: failedNodeId,
            ErrorMessage: error!,
            TriggerOrigin: instance.TriggerOrigin,
            TimestampUtc: instance.UpdatedAt);
    }

    /// <summary>The clean error from a NodeExecutionFailed entry — its <c>Data["error"]</c> if present, else its message.</summary>
    private static string ExtractError(ExecutionJournal entry)
    {
        if (entry.Data is not null && entry.Data.TryGetValue("error", out var raw) && raw is not null)
        {
            var text = raw switch
            {
                string s => s,
                JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
                _ => raw.ToString()
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text!;
            }
        }

        return string.IsNullOrWhiteSpace(entry.Message) ? "Workflow execution failed." : entry.Message;
    }
}
