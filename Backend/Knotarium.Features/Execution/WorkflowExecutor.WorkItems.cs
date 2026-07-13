using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Features.Execution;

public partial class WorkflowExecutor
{
    private async Task ProcessResumeWorkItemAsync(ExecutionWorkItem workItem, CancellationToken cancellationToken)
    {
        var payloadOptions = new JsonSerializerOptions(PersistenceJsonOptions.Default)
        {
            PropertyNameCaseInsensitive = true
        };

        var payload = JsonSerializer.Deserialize<ResumeWorkItemPayload>(workItem.Payload, payloadOptions)
            ?? throw new InvalidOperationException($"Execution work item '{workItem.Id}' payload is invalid.");

        if (string.IsNullOrWhiteSpace(payload.NodeId))
        {
            throw new InvalidOperationException($"Execution work item '{workItem.Id}' is missing a node id.");
        }

        var instance = await _dbContext.ExecutionInstances
            .Include(execution => execution.NodeStates)
            .Include(execution => execution.JournalEntries)
            .FirstOrDefaultAsync(execution => execution.Id == workItem.ExecutionInstanceId, cancellationToken)
            ?? throw new InvalidOperationException($"Execution '{workItem.ExecutionInstanceId}' was not found for work item '{workItem.Id}'.");

        if (payload.WorkflowVersionId.HasValue)
        {
            instance.WorkflowVersionId = new WorkflowVersionId(payload.WorkflowVersionId.Value);
        }

        var plan = await _planLoader.LoadAsync(instance, cancellationToken);
        if (plan is null)
        {
            return;
        }

        var foldedJournal = new JournalFoldService().FoldJournal(instance.JournalEntries);
        RehydrateGlobalVariables(instance, foldedJournal.Variables);

        var waitingNodeId = NodeId.Create(payload.NodeId);
        var waitingNode = instance.NodeStates.FirstOrDefault(nodeState => nodeState.NodeId == waitingNodeId)
            ?? throw new InvalidOperationException($"Waiting node '{waitingNodeId.Value}' was not found for execution '{instance.Id.Value}'.");

        if (waitingNode.Status == NodeStatus.Completed)
        {
            return;
        }

        if (waitingNode.Status != NodeStatus.Waiting)
        {
            throw new InvalidOperationException(
                $"Execution '{instance.Id.Value}' cannot resume node '{waitingNode.NodeId.Value}' from status '{waitingNode.Status}'.");
        }

        var resumeOutputs = CreateResumeOutputs(payload.Output);
        waitingNode.Status = NodeStatus.Completed;
        waitingNode.ErrorMessage = null;
        waitingNode.Outputs.Remove("eventName");
        foreach (var output in resumeOutputs)
        {
            waitingNode.Outputs[output.Key] = output.Value;
        }

        instance.Status = ExecutionStatus.Running;
        instance.UpdatedAt = _timeProvider.GetUtcNow();

        await _journal.PublishAsync(
            instance,
            "NodeResumed",
            $"Node '{waitingNode.NodeId}' resumed from work item '{workItem.Id}'.",
            nodeId: waitingNode.NodeId,
            data: new Dictionary<string, object>(resumeOutputs, StringComparer.OrdinalIgnoreCase),
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        var scheduledNodes = new Queue<NodeId>();
        foreach (var edge in plan.Edges.Where(edge => edge.From == waitingNode.NodeId))
        {
            scheduledNodes.Enqueue(edge.To);
        }

        await ExecuteScheduledNodesAsync(instance, plan, scheduledNodes, cancellationToken);

        if (instance.Status == ExecutionStatus.Running)
        {
            instance.Status = ExecutionStatus.Completed;
            instance.UpdatedAt = _timeProvider.GetUtcNow();

            await _journal.PublishAsync(instance, JournalEventTypes.WorkflowCompleted, "Workflow run completed successfully.", cancellationToken: cancellationToken);
            _telemetry.RecordExecutionCompleted();

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        else if (instance.Status == ExecutionStatus.Failed)
        {
            _telemetry.RecordExecutionFailed();
        }
    }

    private async Task ProcessRetryWorkItemAsync(ExecutionWorkItem workItem, CancellationToken cancellationToken)
    {
        var payloadOptions = new JsonSerializerOptions(PersistenceJsonOptions.Default)
        {
            PropertyNameCaseInsensitive = true
        };

        var payload = JsonSerializer.Deserialize<RetryWorkItemPayload>(workItem.Payload, payloadOptions)
            ?? throw new InvalidOperationException($"Execution work item '{workItem.Id}' payload is invalid.");

        if (string.IsNullOrWhiteSpace(payload.NodeId))
        {
            throw new InvalidOperationException($"Execution work item '{workItem.Id}' is missing a node id.");
        }

        var instance = await _dbContext.ExecutionInstances
            .Include(execution => execution.NodeStates)
            .Include(execution => execution.JournalEntries)
            .FirstOrDefaultAsync(execution => execution.Id == workItem.ExecutionInstanceId, cancellationToken)
            ?? throw new InvalidOperationException($"Execution '{workItem.ExecutionInstanceId}' was not found for work item '{workItem.Id}'.");

        if (payload.WorkflowVersionId.HasValue)
        {
            instance.WorkflowVersionId = new WorkflowVersionId(payload.WorkflowVersionId.Value);
        }

        var plan = await _planLoader.LoadAsync(instance, cancellationToken);
        if (plan is null)
        {
            return;
        }

        var retryNodeId = NodeId.Create(payload.NodeId);
        var retryNode = instance.NodeStates.FirstOrDefault(nodeState => nodeState.NodeId == retryNodeId)
            ?? throw new InvalidOperationException($"Retry node '{retryNodeId.Value}' was not found for execution '{instance.Id.Value}'.");

        retryNode.Status = NodeStatus.Pending;
        retryNode.ErrorMessage = null;
        retryNode.Outputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        instance.Status = ExecutionStatus.Running;
        instance.UpdatedAt = _timeProvider.GetUtcNow();

        await _journal.PublishAsync(
            instance,
            "NodeRetryStarted",
            $"Retry attempt {payload.AttemptNumber} started for node '{retryNode.NodeId}'.",
            nodeId: retryNode.NodeId,
            data: new Dictionary<string, object>
            {
                ["AttemptNumber"] = payload.AttemptNumber
            },
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        var scheduledNodes = new Queue<NodeId>();
        scheduledNodes.Enqueue(retryNode.NodeId);

        await ExecuteScheduledNodesAsync(instance, plan, scheduledNodes, cancellationToken);

        await CompleteExecutionIfStillRunningAsync(instance, cancellationToken);

        if (instance.Status == ExecutionStatus.Failed)
        {
            _telemetry.RecordExecutionFailed();
        }
    }

    private async Task ProcessReplayWorkItemAsync(ExecutionWorkItem workItem, CancellationToken cancellationToken)
    {
        var payloadOptions = new JsonSerializerOptions(PersistenceJsonOptions.Default)
        {
            PropertyNameCaseInsensitive = true
        };

        var payload = JsonSerializer.Deserialize<ReplayWorkItemPayload>(workItem.Payload, payloadOptions)
            ?? throw new InvalidOperationException($"Execution work item '{workItem.Id}' payload is invalid.");

        if (string.IsNullOrWhiteSpace(payload.NodeId))
        {
            throw new InvalidOperationException($"Execution work item '{workItem.Id}' is missing a node id.");
        }

        var instance = await _dbContext.ExecutionInstances
            .Include(execution => execution.NodeStates)
            .Include(execution => execution.JournalEntries)
            .FirstOrDefaultAsync(execution => execution.Id == workItem.ExecutionInstanceId, cancellationToken)
            ?? throw new InvalidOperationException($"Execution '{workItem.ExecutionInstanceId}' was not found for work item '{workItem.Id}'.");

        if (payload.WorkflowVersionId.HasValue)
        {
            instance.WorkflowVersionId = new WorkflowVersionId(payload.WorkflowVersionId.Value);
        }

        var plan = await _planLoader.LoadAsync(instance, cancellationToken);
        if (plan is null)
        {
            return;
        }

        // The replay instance was created with its cut-point GlobalVariables and seeded upstream
        // node states already persisted, so no journal folding is needed. The cut-point node and
        // its forward closure have no seeded state and are created fresh by the engine.
        var fromNodeId = NodeId.Create(payload.NodeId);

        // Mock-side-effects mode: load the source run's node outputs so non-idempotent nodes can
        // replay their original output instead of firing the real effect.
        _mockSideEffects = payload.MockSideEffects;
        _mockSourceOutputs = null;
        if (_mockSideEffects && instance.ReplayOfExecutionId.HasValue)
        {
            var sourceExecutionId = instance.ReplayOfExecutionId.Value;
            var sourceStates = await _dbContext.NodeStates
                .Where(state => state.ExecutionInstanceId == sourceExecutionId)
                .ToListAsync(cancellationToken);

            _mockSourceOutputs = sourceStates
                .GroupBy(state => state.NodeId)
                .ToDictionary(group => group.Key, group => group.First().Outputs);
        }

        instance.Status = ExecutionStatus.Running;
        instance.UpdatedAt = _timeProvider.GetUtcNow();

        await _journal.PublishAsync(
            instance,
            "ReplayStarted",
            $"Replay started from node '{fromNodeId.Value}'" +
            (instance.ReplayOfExecutionId.HasValue ? $" (replay of '{instance.ReplayOfExecutionId.Value.Value}')." : "."),
            nodeId: fromNodeId,
            cancellationToken: cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        var scheduledNodes = new Queue<NodeId>();
        scheduledNodes.Enqueue(fromNodeId);

        await ExecuteScheduledNodesAsync(instance, plan, scheduledNodes, cancellationToken);

        await CompleteExecutionIfStillRunningAsync(instance, cancellationToken);

        if (instance.Status == ExecutionStatus.Failed)
        {
            _telemetry.RecordExecutionFailed();
        }
    }

    private async Task ProcessManualDecisionWorkItemAsync(ExecutionWorkItem workItem, CancellationToken cancellationToken)
    {
        var payloadOptions = new JsonSerializerOptions(PersistenceJsonOptions.Default)
        {
            PropertyNameCaseInsensitive = true
        };

        var payload = JsonSerializer.Deserialize<ManualDecisionWorkItemPayload>(workItem.Payload, payloadOptions)
            ?? throw new InvalidOperationException($"Execution work item '{workItem.Id}' payload is invalid.");

        if (string.IsNullOrWhiteSpace(payload.NodeId) ||
            !ManualDecisions.TryNormalize(payload.Decision, out var decision))
        {
            throw new InvalidOperationException($"Execution work item '{workItem.Id}' is missing manual decision metadata.");
        }

        var instance = await _dbContext.ExecutionInstances
            .Include(execution => execution.NodeStates)
            .Include(execution => execution.JournalEntries)
            .FirstOrDefaultAsync(execution => execution.Id == workItem.ExecutionInstanceId, cancellationToken)
            ?? throw new InvalidOperationException($"Execution '{workItem.ExecutionInstanceId}' was not found for work item '{workItem.Id}'.");

        if (payload.WorkflowVersionId.HasValue)
        {
            instance.WorkflowVersionId = new WorkflowVersionId(payload.WorkflowVersionId.Value);
        }

        var plan = await _planLoader.LoadAsync(instance, cancellationToken);
        if (plan is null)
        {
            return;
        }

        var foldedJournal = new JournalFoldService().FoldJournal(instance.JournalEntries);
        RehydrateGlobalVariables(instance, foldedJournal.Variables);

        var manualNodeId = NodeId.Create(payload.NodeId);
        var manualNode = instance.NodeStates.FirstOrDefault(nodeState => nodeState.NodeId == manualNodeId)
            ?? throw new InvalidOperationException($"Manual decision node '{manualNodeId.Value}' was not found for execution '{instance.Id.Value}'.");

        if (manualNode.Status == NodeStatus.Completed || manualNode.Status == NodeStatus.Failed)
        {
            return;
        }

        if (manualNode.Status != NodeStatus.RequiresManualDecision)
        {
            throw new InvalidOperationException(
                $"Execution '{instance.Id.Value}' cannot apply manual decision to node '{manualNode.NodeId.Value}' from status '{manualNode.Status}'.");
        }

        var pendingAttemptId = ExecutionJournalData.FindPendingAttemptId(instance.JournalEntries, manualNode.NodeId);
        if (!string.IsNullOrWhiteSpace(payload.ExpectedAttemptId) &&
            !string.Equals(payload.ExpectedAttemptId, pendingAttemptId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var scheduledNodes = new Queue<NodeId>();
        var attemptData = ExecutionJournalData.CreateAttemptData(payload.Reason, pendingAttemptId);

        switch (decision)
        {
            case ManualDecision.Retry:
                manualNode.Status = NodeStatus.Pending;
                manualNode.ErrorMessage = null;
                manualNode.Outputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                await _journal.PublishAsync(
                    instance,
                    JournalEventTypes.NodeExecutionFailed,
                    $"Manual retry accepted for node '{manualNode.NodeId.Value}'. Previous interrupted attempt closed.",
                    nodeId: manualNode.NodeId,
                    data: attemptData,
                    cancellationToken: cancellationToken);

                instance.Status = ExecutionStatus.Running;
                instance.UpdatedAt = _timeProvider.GetUtcNow();
                await _dbContext.SaveChangesAsync(cancellationToken);

                scheduledNodes.Enqueue(manualNode.NodeId);
                await ExecuteScheduledNodesAsync(instance, plan, scheduledNodes, cancellationToken);
                await CompleteExecutionIfStillRunningAsync(instance, cancellationToken);
                break;

            case ManualDecision.Skip:
                manualNode.Status = NodeStatus.Completed;
                manualNode.ErrorMessage = null;
                manualNode.Outputs["manualDecision"] = "Skip";
                manualNode.Outputs["skipped"] = true;

                var completionData = new Dictionary<string, object>(attemptData, StringComparer.OrdinalIgnoreCase)
                {
                    ["manualDecision"] = "Skip",
                    ["skipped"] = true
                };

                await _journal.PublishAsync(
                    instance,
                    JournalEventTypes.NodeExecutionCompleted,
                    $"Node '{manualNode.NodeId.Value}' was manually skipped by an operator.",
                    nodeId: manualNode.NodeId,
                    data: completionData,
                    cancellationToken: cancellationToken);

                instance.Status = ExecutionStatus.Running;
                instance.UpdatedAt = _timeProvider.GetUtcNow();
                await _dbContext.SaveChangesAsync(cancellationToken);

                foreach (var edge in plan.Edges.Where(edge => edge.From == manualNode.NodeId))
                {
                    scheduledNodes.Enqueue(edge.To);
                }

                await ExecuteScheduledNodesAsync(instance, plan, scheduledNodes, cancellationToken);
                await CompleteExecutionIfStillRunningAsync(instance, cancellationToken);
                break;

            case ManualDecision.Fail:
                manualNode.Status = NodeStatus.Failed;
                manualNode.ErrorMessage = string.IsNullOrWhiteSpace(payload.Reason)
                    ? "Operator marked node as failed."
                    : payload.Reason;

                var failureData = ExecutionJournalData.CreateAttemptData(payload.Reason, pendingAttemptId);
                failureData["error"] = manualNode.ErrorMessage;
                failureData["manualDecision"] = "Fail";

                await _journal.PublishAsync(
                    instance,
                    JournalEventTypes.NodeExecutionFailed,
                    $"Node '{manualNode.NodeId.Value}' was manually failed by an operator.",
                    nodeId: manualNode.NodeId,
                    data: failureData,
                    cancellationToken: cancellationToken);

                instance.Status = ExecutionStatus.Running;
                instance.UpdatedAt = _timeProvider.GetUtcNow();

                await HandleNodeFailureAsync(instance, plan, manualNode, scheduledNodes, cancellationToken);
                if (scheduledNodes.Count > 0 && instance.Status != ExecutionStatus.Failed)
                {
                    await ExecuteScheduledNodesAsync(instance, plan, scheduledNodes, cancellationToken);
                    await CompleteExecutionIfStillRunningAsync(instance, cancellationToken);
                }

                if (instance.Status == ExecutionStatus.Failed)
                {
                    _telemetry.RecordExecutionFailed();
                }

                break;
        }
    }

    private async Task CompleteExecutionIfStillRunningAsync(ExecutionInstance instance, CancellationToken cancellationToken)
    {
        if (instance.Status == ExecutionStatus.Running)
        {
            instance.Status = ExecutionStatus.Completed;
            instance.UpdatedAt = _timeProvider.GetUtcNow();

            await _journal.PublishAsync(instance, JournalEventTypes.WorkflowCompleted, "Workflow run completed successfully.", cancellationToken: cancellationToken);
            _telemetry.RecordExecutionCompleted();

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static void RehydrateGlobalVariables(ExecutionInstance instance, IReadOnlyDictionary<string, JsonElement> variables)
    {
        if (variables.Count == 0)
        {
            return;
        }

        foreach (var variable in variables)
        {
            if (variable.Key.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            instance.GlobalVariables[variable.Key] = variable.Value.Clone();
        }
    }

    private static Dictionary<string, object> CreateResumeOutputs(JsonElement output)
    {
        var normalizedOutput = output.ValueKind == JsonValueKind.Undefined ? JsonDocument.Parse("null").RootElement.Clone() : output.Clone();
        var outputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["output"] = normalizedOutput,
            ["result"] = normalizedOutput,
            ["payload"] = normalizedOutput
        };

        if (normalizedOutput.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in normalizedOutput.EnumerateObject())
            {
                outputs[property.Name] = property.Value.Clone();
            }
        }

        return outputs;
    }
}
