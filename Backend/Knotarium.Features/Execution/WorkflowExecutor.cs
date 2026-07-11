using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Security;

namespace Knotarium.Features.Execution;

public partial class WorkflowExecutor
{
    private readonly AppDbContext _dbContext;
    private readonly WorkflowCompiler _compiler;
    private readonly INodeTaskRegistry _registry;
    private readonly IExecutionEventPublisher _publisher;
    private readonly IExecutionJournalWriter _journalWriter;
    private readonly ExecutionTelemetry _telemetry;
    private readonly ICorrelationTokenCrypto _correlationTokenCrypto;
    private readonly TimeProvider _timeProvider;
    private readonly IFailureAlertSink? _failureAlertQueue;
    private readonly IErrorWorkflowSink? _errorWorkflowQueue;

    // Replay mock-side-effects mode (set per Replay work item). When enabled, a non-idempotent
    // node replays its original output from the source run instead of firing the real effect.
    private bool _mockSideEffects;
    private IReadOnlyDictionary<NodeId, Dictionary<string, object>>? _mockSourceOutputs;

    public WorkflowExecutor(
        AppDbContext dbContext,
        WorkflowCompiler compiler,
        INodeTaskRegistry registry,
        IExecutionEventPublisher publisher,
        IExecutionJournalWriter journalWriter,
        ExecutionTelemetry? telemetry = null,
        ICorrelationTokenCrypto? correlationTokenCrypto = null,
        TimeProvider? timeProvider = null,
        IFailureAlertSink? failureAlertQueue = null,
        IErrorWorkflowSink? errorWorkflowQueue = null)
    {
        _dbContext = dbContext;
        _compiler = compiler;
        _registry = registry;
        _publisher = publisher;
        _journalWriter = journalWriter;
        _telemetry = telemetry ?? new ExecutionTelemetry();
        _correlationTokenCrypto = correlationTokenCrypto ?? new CorrelationTokenCrypto();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _failureAlertQueue = failureAlertQueue;
        _errorWorkflowQueue = errorWorkflowQueue;
    }

    public async Task<bool> ResumeWorkflowTransactionAsync(
        string rawToken,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        var hashedToken = _correlationTokenCrypto.HashToken(rawToken);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var affected = await _dbContext.CorrelationTokens
                .Where(token => token.HashedToken == hashedToken && token.ConsumedAtUtc == null && token.ExpiresAtUtc > now)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(token => token.ConsumedAtUtc, now),
                    cancellationToken);

            if (affected != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            var token = await _dbContext.CorrelationTokens
                .SingleOrDefaultAsync(correlationToken => correlationToken.HashedToken == hashedToken, cancellationToken);

            if (token is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            var instance = await _dbContext.ExecutionInstances
                .SingleOrDefaultAsync(execution => execution.Id == token.ExecutionInstanceId, cancellationToken);

            if (instance is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            if (!instance.WorkflowVersionId.HasValue)
            {
                throw new InvalidOperationException($"Execution '{instance.Id.Value}' cannot resume without a bound workflow version.");
            }

            var workflowVersion = await _dbContext.WorkflowVersions
                .SingleOrDefaultAsync(version => version.Id == instance.WorkflowVersionId.Value, cancellationToken);

            if (workflowVersion is null)
            {
                throw new InvalidOperationException(
                    $"Execution '{instance.Id.Value}' cannot resume because workflow version '{instance.WorkflowVersionId.Value.Value}' is missing.");
            }

            instance.Status = ExecutionStatus.Running;
            instance.UpdatedAt = now;

            var outputData = JsonSerializer.Deserialize<object>(payload.GetRawText(), PersistenceJsonOptions.Default);
            var journalEntry = new ExecutionJournal
            {
                Id = Guid.NewGuid(),
                ExecutionInstanceId = instance.Id,
                NodeId = token.NodeId,
                Timestamp = now,
                EventType = JournalEventTypes.WorkflowResumed,
                Message = $"Workflow resume registered for node '{token.NodeId.Value}'.",
                Data = new Dictionary<string, object>
                {
                    ["Output"] = outputData ?? payload,
                    ["WorkflowVersionId"] = workflowVersion.Id.Value,
                    ["WorkItemType"] = "Resume"
                }
            };

            var workItem = new ExecutionWorkItem
            {
                Id = Guid.NewGuid(),
                ExecutionInstanceId = instance.Id,
                Type = "Resume",
                Payload = JsonSerializer.Serialize(new
                {
                    nodeId = token.NodeId.Value,
                    workflowVersionId = workflowVersion.Id.Value,
                    output = payload
                }),
                Status = WorkItemStatus.Pending,
                CreatedAtUtc = now,
                NotBeforeUtc = null,
                ProcessedAtUtc = null
            };

            await _dbContext.JournalEntries.AddAsync(journalEntry, cancellationToken);
            await _dbContext.ExecutionWorkItems.AddAsync(workItem, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Records a manual operator decision for a node that requires intervention and queues a continuation work item.
    /// </summary>
    /// <param name="executionId">The execution instance identifier.</param>
    /// <param name="nodeId">The node identifier requiring a manual decision.</param>
    /// <param name="decision">The operator decision: Retry, Skip, or Fail.</param>
    /// <param name="reason">An optional reason provided by the operator.</param>
    /// <param name="expectedAttemptId">The expected interrupted attempt identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when the decision is accepted; otherwise, <see langword="false"/>.</returns>
    public async Task<bool> ApplyManualDecisionAsync(
        Guid executionId,
        string nodeId,
        string decision,
        string? reason,
        string? expectedAttemptId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nodeId) ||
            !TryNormalizeManualDecision(decision, out var normalizedDecision))
        {
            return false;
        }

        var executionInstanceId = new ExecutionInstanceId(executionId);
        var manualNodeId = NodeId.Create(nodeId);
        var now = _timeProvider.GetUtcNow();

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var instance = await _dbContext.ExecutionInstances
                .Include(execution => execution.NodeStates)
                .Include(execution => execution.JournalEntries)
                .FirstOrDefaultAsync(execution => execution.Id == executionInstanceId, cancellationToken);

            if (instance is null || !instance.WorkflowVersionId.HasValue)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            var manualNode = instance.NodeStates.FirstOrDefault(nodeState => nodeState.NodeId == manualNodeId);
            if (manualNode is null || manualNode.Status != NodeStatus.RequiresManualDecision)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            var pendingAttemptId = FindPendingAttemptId(instance.JournalEntries, manualNode.NodeId);
            if (!string.IsNullOrWhiteSpace(expectedAttemptId) &&
                !string.Equals(expectedAttemptId, pendingAttemptId, StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            instance.Status = normalizedDecision == ManualDecision.Fail ? ExecutionStatus.Failed : ExecutionStatus.Running;
            instance.UpdatedAt = now;

            var journalEntry = new ExecutionJournal
            {
                Id = Guid.NewGuid(),
                ExecutionInstanceId = instance.Id,
                NodeId = manualNode.NodeId,
                Timestamp = now,
                EventType = JournalEventTypes.ManualDecisionRecorded,
                Message = $"Manual decision '{normalizedDecision}' recorded for node '{manualNode.NodeId.Value}'.",
                Data = new Dictionary<string, object>
                {
                    ["Decision"] = normalizedDecision.ToString(),
                    ["Reason"] = reason ?? string.Empty,
                    ["ExpectedAttemptId"] = expectedAttemptId ?? string.Empty,
                    ["AttemptId"] = pendingAttemptId ?? string.Empty,
                    ["WorkItemType"] = "ManualDecision"
                }
            };

            var workItem = new ExecutionWorkItem
            {
                Id = Guid.NewGuid(),
                ExecutionInstanceId = instance.Id,
                Type = "ManualDecision",
                Payload = JsonSerializer.Serialize(new ManualDecisionWorkItemPayload(
                    manualNode.NodeId.Value,
                    normalizedDecision.ToString(),
                    reason,
                    pendingAttemptId,
                    instance.WorkflowVersionId.Value.Value)),
                Status = WorkItemStatus.Pending,
                CreatedAtUtc = now,
                NotBeforeUtc = null,
                ProcessedAtUtc = null
            };

            await _dbContext.JournalEntries.AddAsync(journalEntry, cancellationToken);
            await _dbContext.ExecutionWorkItems.AddAsync(workItem, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task ProcessWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default)
    {
        var workItem = await _dbContext.ExecutionWorkItems
            .SingleOrDefaultAsync(item => item.Id == workItemId, cancellationToken);

        if (workItem is null)
        {
            return;
        }

        if (await IsExecutionCancelledAsync(workItem.ExecutionInstanceId, cancellationToken))
        {
            // The owning execution was cancelled; consume the work item without resuming.
            workItem.Status = WorkItemStatus.Completed;
            workItem.ProcessedAtUtc = _timeProvider.GetUtcNow();
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            switch (workItem.Type)
            {
                case "Resume":
                    await ProcessResumeWorkItemAsync(workItem, cancellationToken);
                    break;

                case "Retry":
                    await ProcessRetryWorkItemAsync(workItem, cancellationToken);
                    break;

                case "Replay":
                    await ProcessReplayWorkItemAsync(workItem, cancellationToken);
                    break;

                case "ManualDecision":
                    await ProcessManualDecisionWorkItemAsync(workItem, cancellationToken);
                    break;

                default:
                    throw new NotSupportedException($"Execution work item type '{workItem.Type}' is not supported.");
            }

            workItem.Status = WorkItemStatus.Completed;
            workItem.ProcessedAtUtc = _timeProvider.GetUtcNow();
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            workItem.Status = WorkItemStatus.Failed;
            workItem.ProcessedAtUtc = _timeProvider.GetUtcNow();
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private async Task<ExecutionJournal> PublishJournalEntryAsync(
        ExecutionInstance instance,
        string eventType,
        string message,
        NodeId? nodeId = null,
        Dictionary<string, object>? data = null,
        CancellationToken cancellationToken = default)
    {
        var entry = new ExecutionJournal
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = instance.Id,
            NodeId = nodeId,
            Timestamp = DateTimeOffset.UtcNow,
            EventType = eventType,
            Message = message,
            Data = data ?? new Dictionary<string, object>()
        };

        // Write directly to IExecutionJournalWriter bypassing EF Core change-tracking overhead on hot-path
        await _journalWriter.WriteAsync(entry);

        await _publisher.PublishAsync(instance.Id, entry, cancellationToken);

        // Single failure chokepoint: every WorkflowFailed path flows through here. Enqueue is a
        // non-blocking in-memory hand-off, so it can never block or break the run.
        if (eventType == JournalEventTypes.WorkflowFailed)
        {
            _failureAlertQueue?.Enqueue(instance.Id);
            _errorWorkflowQueue?.Enqueue(instance.Id);
        }

        return entry;
    }

    public async Task ExecuteAsync(
        ExecutionInstanceId executionId, 
        string? resumeEventName = null, 
        Dictionary<string, object>? eventData = null, 
        CancellationToken cancellationToken = default)
    {
        var startedExecution = false;
        var finishedExecution = false;

        var instance = await _dbContext.ExecutionInstances
            .Include(e => e.NodeStates)
            .Include(e => e.JournalEntries)
            .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken);

        if (instance == null)
        {
            return;
        }

        if (instance.Status == ExecutionStatus.Running)
        {
            // Already running
            return;
        }

        if (instance.Status == ExecutionStatus.Cancelled)
        {
            // The execution was cancelled (e.g. its workflow was deactivated) before it started.
            return;
        }

        using var workflowActivity = _telemetry.StartWorkflowActivity(instance, string.IsNullOrEmpty(resumeEventName) ? "start" : "resume");

        var plan = await LoadExecutionPlanAsync(instance, cancellationToken);
        if (plan is null)
        {
            return;
        }

        var scheduledNodes = new Queue<NodeId>();
        bool isResume = !string.IsNullOrEmpty(resumeEventName);

        if (isResume)
        {
            var waitingNode = instance.NodeStates.FirstOrDefault(ns =>
            {
                if (ns.Status != NodeStatus.Waiting) return false;
                if (!ns.Outputs.TryGetValue("eventName", out var evObj) || evObj == null) return false;
                var evName = evObj is string str ? str : evObj is System.Text.Json.JsonElement elem && elem.ValueKind == System.Text.Json.JsonValueKind.String ? elem.GetString() : evObj.ToString();
                return evName != null && evName.Equals(resumeEventName, StringComparison.OrdinalIgnoreCase);
            });

            if (waitingNode != null)
            {
                waitingNode.Status = NodeStatus.Completed;
                waitingNode.Outputs.Remove("eventName");
                if (eventData != null)
                {
                    foreach (var kvp in eventData)
                    {
                        waitingNode.Outputs[kvp.Key] = kvp.Value;
                    }
                }

                instance.Status = ExecutionStatus.Running;
                instance.UpdatedAt = DateTimeOffset.UtcNow;
                await PublishJournalEntryAsync(instance, "NodeResumed", $"Node '{waitingNode.NodeId}' resumed on event '{resumeEventName}' and completed successfully.", nodeId: waitingNode.NodeId, cancellationToken: cancellationToken);

                var outgoingEdges = plan.Edges.Where(e => e.From == waitingNode.NodeId);
                foreach (var edge in outgoingEdges)
                {
                    scheduledNodes.Enqueue(edge.To);
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                // No node was waiting for this event; exit early
                return;
            }
        }
        else
        {
            // Fresh run
            instance.Status = ExecutionStatus.Running;
            instance.UpdatedAt = DateTimeOffset.UtcNow;
            _telemetry.RecordExecutionStarted();
            startedExecution = true;
            await PublishJournalEntryAsync(instance, "WorkflowStarted", $"Workflow run started for definition '{instance.WorkflowDefinitionId}'.", cancellationToken: cancellationToken);

            var entryNodeIds = await ResolveEntryNodesForTriggerOriginAsync(plan, instance, cancellationToken);
            foreach (var entryNodeId in entryNodeIds)
            {
                scheduledNodes.Enqueue(entryNodeId);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await ExecuteScheduledNodesAsync(instance, plan, scheduledNodes, cancellationToken);

        if (instance.Status == ExecutionStatus.Running)
        {
            instance.Status = ExecutionStatus.Completed;
            instance.UpdatedAt = DateTimeOffset.UtcNow;

            await PublishJournalEntryAsync(instance, "WorkflowCompleted", "Workflow run completed successfully.", cancellationToken: cancellationToken);
            _telemetry.RecordExecutionCompleted();
            finishedExecution = true;

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        if (instance.Status == ExecutionStatus.Failed)
        {
            _telemetry.RecordExecutionFailed();
            finishedExecution = true;
        }

        if (startedExecution && !finishedExecution)
        {
            _telemetry.RecordExecutionStopped();
        }
    }

    private async Task ExecuteScheduledNodesAsync(
        ExecutionInstance instance,
        ExecutionPlan plan,
        Queue<NodeId> scheduledNodes,
        CancellationToken cancellationToken)
    {
        var visitedInThisSession = new HashSet<NodeId>();

        while (scheduledNodes.Count > 0)
        {
            // Cooperative cancellation: the deactivate path writes Cancelled directly to the
            // database, so re-read the live status between nodes. The currently running node
            // (if any) finishes; we simply stop scheduling further work.
            if (await IsExecutionCancelledAsync(instance.Id, cancellationToken))
            {
                await MarkExecutionCancelledAsync(instance, cancellationToken);
                return;
            }

            var currentNodeId = scheduledNodes.Dequeue();

            object? loopbackEndValue = null;
            bool hasLoopbackEndValue = false;

            var plannedNode = plan.Nodes.FirstOrDefault(node => node.Id == currentNodeId);
            if (plannedNode != null && plannedNode.Type.Equals("forLoop", StringComparison.OrdinalIgnoreCase))
            {
                var loopNodeState = instance.NodeStates.FirstOrDefault(state => state.NodeId == currentNodeId);
                if (loopNodeState != null)
                {
                    var loopIncomingEdges = plan.Edges.Where(edge => edge.To == currentNodeId);
                    bool isLoopbackTrigger = false;
                    foreach (var edge in loopIncomingEdges)
                    {
                        if (edge.Input.Equals("end", StringComparison.OrdinalIgnoreCase))
                        {
                            var pred = instance.NodeStates.FirstOrDefault(state => state.NodeId == edge.From);
                            if (pred != null && pred.Status == NodeStatus.Completed)
                            {
                                isLoopbackTrigger = true;
                            }
                        }
                    }

                    if (isLoopbackTrigger)
                    {
                        var endEdge = loopIncomingEdges.FirstOrDefault(edge =>
                            edge.Input.Equals("end", StringComparison.OrdinalIgnoreCase) &&
                            instance.NodeStates.Any(state => state.NodeId == edge.From && state.Status == NodeStatus.Completed));
                        if (endEdge != null)
                        {
                            var pred = instance.NodeStates.First(state => state.NodeId == endEdge.From);
                            pred.Outputs.TryGetValue(endEdge.Output, out var value);
                            loopbackEndValue = value;
                            hasLoopbackEndValue = true;
                        }

                        loopNodeState.Status = NodeStatus.Pending;
                        visitedInThisSession.Remove(currentNodeId);
                        ResetLoopBodyNodes(instance, plan, currentNodeId, visitedInThisSession);
                    }
                }
            }

            // Fan-in / Join: wait for ALL incoming branches before running. If any incoming-edge
            // predecessor has not completed yet, defer WITHOUT marking the node visited — a later
            // branch will re-enqueue this join when it completes, and the last one to finish finds
            // every predecessor done and lets it through. (See JoinNodeTask for the semantics.)
            if (plannedNode != null && plannedNode.Type.Equals("join", StringComparison.OrdinalIgnoreCase))
            {
                var joinIncomingEdges = plan.Edges.Where(edge => edge.To == currentNodeId).ToList();
                var allBranchesReady = joinIncomingEdges.All(edge =>
                    instance.NodeStates.Any(state => state.NodeId == edge.From && state.Status == NodeStatus.Completed));

                if (joinIncomingEdges.Count > 0 && !allBranchesReady)
                {
                    continue;
                }
            }

            if (!visitedInThisSession.Add(currentNodeId))
            {
                continue;
            }
            if (plannedNode == null)
            {
                continue;
            }

            var nodeState = instance.NodeStates.FirstOrDefault(state => state.NodeId == currentNodeId);
            if (nodeState == null)
            {
                nodeState = new NodeState
                {
                    Id = Guid.NewGuid(),
                    ExecutionInstanceId = instance.Id,
                    NodeId = currentNodeId,
                    Status = NodeStatus.Pending,
                    ExecutionCount = 0
                };
                instance.NodeStates.Add(nodeState);
            }

            if (nodeState.Status == NodeStatus.Completed)
            {
                var outgoingEdges = plan.Edges.Where(edge => edge.From == currentNodeId);
                string? selectedPort = null;
                if ((plannedNode.Type.Equals("condition", StringComparison.OrdinalIgnoreCase) ||
                     plannedNode.Type.Equals("forLoop", StringComparison.OrdinalIgnoreCase) ||
                     plannedNode.Type.Equals("parallelForEach", StringComparison.OrdinalIgnoreCase)) &&
                    nodeState.Outputs.TryGetValue("selectedPort", out var portObj) &&
                    portObj != null)
                {
                    selectedPort = portObj is string portStr
                        ? portStr
                        : portObj is JsonElement selectedPortElement && selectedPortElement.ValueKind == JsonValueKind.String
                            ? selectedPortElement.GetString()
                            : portObj.ToString();
                }

                foreach (var edge in outgoingEdges)
                {
                    if (selectedPort != null && !edge.Output.Equals(selectedPort, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    scheduledNodes.Enqueue(edge.To);
                }

                continue;
            }

            if (nodeState.Status == NodeStatus.Waiting || nodeState.Status == NodeStatus.Failed || nodeState.Status == NodeStatus.RequiresManualDecision)
            {
                continue;
            }

            var manifest = await GetManifestAsync(plannedNode.Type, cancellationToken);
            var task = _registry.GetTask(plannedNode.Type);
            if (manifest?.TriggerOnly == true && task == null)
            {
                await CompleteTriggerNodeAsync(instance, plan, nodeState, plannedNode, scheduledNodes, cancellationToken);
                continue;
            }

            var nodeInputs = new Dictionary<string, object>(plannedNode.Properties, StringComparer.OrdinalIgnoreCase);
            var incomingEdges = plan.Edges.Where(edge => edge.To == currentNodeId);
            foreach (var edge in incomingEdges)
            {
                var predecessorState = instance.NodeStates.FirstOrDefault(state => state.NodeId == edge.From);
                if (predecessorState != null &&
                    predecessorState.Status == NodeStatus.Completed &&
                    predecessorState.Outputs.TryGetValue(edge.Output, out var value))
                {
                    nodeInputs[edge.Input] = value;
                }
            }

            if (plannedNode.Type.Equals("forLoop", StringComparison.OrdinalIgnoreCase) && hasLoopbackEndValue)
            {
                nodeInputs["end"] = loopbackEndValue!;
            }

            // Fan-in / Join: collect every completed branch's output into an ordered "results" array
            // so the JoinNodeTask can expose them downstream. Aggregation is done over incoming edges
            // (not the input map) so multiple branches can share one input socket without clobbering.
            if (plannedNode.Type.Equals("join", StringComparison.OrdinalIgnoreCase))
            {
                var branchResults = new List<object?>();
                foreach (var edge in incomingEdges)
                {
                    var predecessorState = instance.NodeStates.FirstOrDefault(state => state.NodeId == edge.From);
                    if (predecessorState != null &&
                        predecessorState.Status == NodeStatus.Completed &&
                        predecessorState.Outputs.TryGetValue(edge.Output, out var branchValue))
                    {
                        branchResults.Add(branchValue);
                    }
                }
                nodeInputs["results"] = branchResults;
            }

            var stateProjection = new WorkflowStateProjection(instance);
            var nonExpressionParams = NonExpressionParams(manifest);
            var evaluatedInputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in nodeInputs)
            {
                // Expression:false params are handed over unresolved (D7) — primitives still unbox.
                bool resolve = !nonExpressionParams.Contains(input.Key);
                evaluatedInputs[input.Key] = EvaluatePropertyValue(input.Value, stateProjection, resolve)!;
            }

            nodeState.Inputs = evaluatedInputs;
            // Capture an exact snapshot of the global variables as they were when this node
            // started. This is the only new hot-path write for replay / time-travel debugging:
            // it lets a replay restore the cut-point variable state in O(1) without journal folding.
            nodeState.VariablesBefore = JsonSerializer.Serialize(instance.GlobalVariables, PersistenceJsonOptions.Default);
            nodeState.Status = NodeStatus.Running;
            nodeState.ExecutionCount++;
            instance.UpdatedAt = _timeProvider.GetUtcNow();

            await PublishJournalEntryAsync(
                instance,
                JournalEventTypes.NodeExecutionStarted,
                $"Executing node '{currentNodeId}' (type '{plannedNode.Type}').",
                nodeId: currentNodeId,
                cancellationToken: cancellationToken);

            await _dbContext.SaveChangesAsync(cancellationToken);

            // Parallel map (fan-out + wait-for-all fan-in): the executor itself drives the body node
            // once per collection item, concurrently and bounded, then commits the aggregate result.
            // This is the only node whose body runs on multiple threads, which is why it lives here
            // (with all DB/state writes kept on this thread) rather than in an INodeTask.
            if (plannedNode.Type.Equals("parallelForEach", StringComparison.OrdinalIgnoreCase))
            {
                await ExecuteParallelForEachAsync(instance, plan, plannedNode, nodeState, evaluatedInputs, scheduledNodes, cancellationToken);
                continue;
            }

            if (task == null)
            {
                nodeState.Status = NodeStatus.Failed;
                nodeState.ErrorMessage = $"No task implementation registered for type '{plannedNode.Type}'.";

                await PublishJournalEntryAsync(
                    instance,
                    JournalEventTypes.NodeExecutionFailed,
                    $"Failed to execute node '{currentNodeId}': No task registered.",
                    nodeId: currentNodeId,
                    data: new Dictionary<string, object> { ["error"] = nodeState.ErrorMessage },
                    cancellationToken: cancellationToken);

                await HandleNodeFailureAsync(instance, plan, nodeState, scheduledNodes, cancellationToken);
                continue;
            }

            // Per-node execution timeout. Start from the node's OWN manifest default (Delay 60s, HTTP 30s,
            // …) — previously hardcoded to 5s, which wrongly killed any node meant to run longer (e.g. a 5s
            // Delay timed out at 5s). An explicit timeoutSeconds/timeout property still overrides it.
            var timeoutSeconds = manifest is { DefaultTimeoutSeconds: > 0 } ? manifest.DefaultTimeoutSeconds : 5;
            if (plannedNode.Properties.TryGetValue("timeoutSeconds", out var timeoutObj) ||
                plannedNode.Properties.TryGetValue("timeout", out timeoutObj))
            {
                if (timeoutObj != null && int.TryParse(timeoutObj.ToString(), out var parsedSeconds))
                {
                    timeoutSeconds = Math.Clamp(parsedSeconds, 1, 3600);
                }
            }

            // A Delay node's whole purpose is to wait, so its configured duration must never trip the
            // timeout (which exists to catch a HUNG node, not an intentional wait). Guarantee headroom over
            // the delay regardless of the manifest/override above (+2s buffer).
            if (string.Equals(plannedNode.Type, "delay", StringComparison.OrdinalIgnoreCase)
                && plannedNode.Properties.TryGetValue("delayMs", out var delayObj) && delayObj != null
                && double.TryParse(delayObj.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var delayMs)
                && delayMs > 0)
            {
                timeoutSeconds = Math.Max(timeoutSeconds, (int)Math.Ceiling(delayMs / 1000.0) + 2);
            }

            var mockedOutputs = TryGetMockedSideEffectOutputs(currentNodeId, manifest);

            // Design-time pin: on a manual/editor run, a node whose __pinnedOutput is enabled emits its
            // pinned payload instead of executing, so downstream nodes can be built/re-run without
            // re-running upstream. Never honored on active runs, so a pin can't ship to production.
            var pinnedOutputs = mockedOutputs == null && instance.TriggerOrigin.Equals("manual", StringComparison.OrdinalIgnoreCase)
                ? PinnedOutput.TryReadOutputs(plannedNode.Properties.GetValueOrDefault(PinnedOutput.PropertyKey))
                : null;

            var attemptId = mockedOutputs == null && pinnedOutputs == null && manifest?.SideEffectKind == NodeSideEffectKind.NonIdempotentSideEffect
                ? Guid.NewGuid()
                : (Guid?)null;

            if (attemptId.HasValue)
            {
                await PublishJournalEntryAsync(
                    instance,
                    JournalEventTypes.AttemptingExternalEffect,
                    $"Attempting non-idempotent external effect for node '{currentNodeId}'.",
                    nodeId: currentNodeId,
                    data: new Dictionary<string, object>
                    {
                        ["NodeId"] = currentNodeId.Value,
                        ["AttemptId"] = attemptId.Value.ToString(),
                        ["SideEffectKind"] = NodeSideEffectKind.NonIdempotentSideEffect.ToString(),
                        ["StartedAtUtc"] = _timeProvider.GetUtcNow()
                    },
                    cancellationToken: cancellationToken);

                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            LegacyNodeResult result;

            if (mockedOutputs != null)
            {
                // Replay mock-side-effects mode: instead of invoking the task (which would fire the
                // real, non-idempotent effect), short-circuit to the node's original output from the
                // source run. Makes replay safe for pure-logic debugging.
                result = new LegacyNodeResult.Success(mockedOutputs);

                await PublishJournalEntryAsync(
                    instance,
                    JournalEventTypes.NodeExecutionStarted,
                    $"Replay is mocking the non-idempotent side effect for node '{currentNodeId}'; returning its original output.",
                    nodeId: currentNodeId,
                    cancellationToken: cancellationToken);
            }
            else if (pinnedOutputs != null)
            {
                // Design-time pinned output: return the sample instead of executing the task.
                result = new LegacyNodeResult.Success(pinnedOutputs);

                await PublishJournalEntryAsync(
                    instance,
                    JournalEventTypes.NodeExecutionStarted,
                    $"Node '{currentNodeId}' is pinned; returning its pinned output instead of executing.",
                    nodeId: currentNodeId,
                    cancellationToken: cancellationToken);
            }
            else
            {
                var context = new NodeExecutionContext(
                    WorkflowId: instance.WorkflowDefinitionId,
                    ExecutionId: instance.Id.Value,
                    NodeId: currentNodeId,
                    Inputs: evaluatedInputs,
                    GlobalVariables: instance.GlobalVariables,
                    State: stateProjection);

                using var nodeActivity = _telemetry.StartNodeActivity(instance.Id, currentNodeId, plannedNode.Type);
                var nodeExecutionStart = Stopwatch.GetTimestamp();

                using (var timeoutCts = new CancellationTokenSource())
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
                {
                    try
                    {
                        var executionTask = task.ExecuteAsync(context, linkedCts.Token);
                        var delayTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);

                        var completedTask = await Task.WhenAny(executionTask, delayTask);
                        if (completedTask == executionTask)
                        {
                            timeoutCts.Cancel();
                            result = await executionTask;
                        }
                        else
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                            }

                            timeoutCts.Cancel();
                            nodeState.Status = NodeStatus.Failed;
                            nodeState.ErrorMessage = $"Execution timed out after {timeoutSeconds}s.";

                            await PublishJournalEntryAsync(
                                instance,
                                JournalEventTypes.NodeExecutionFailed,
                                $"Node '{currentNodeId}' execution timed out after {timeoutSeconds} seconds.",
                                nodeId: currentNodeId,
                                data: CreateFailureJournalData(nodeState.ErrorMessage, attemptId),
                                cancellationToken: cancellationToken);

                            await HandleNodeFailureAsync(instance, plan, nodeState, scheduledNodes, cancellationToken);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        result = new LegacyNodeResult.Failure(ex.Message);
                    }
                }

                var nodeExecutionElapsed = Stopwatch.GetElapsedTime(nodeExecutionStart);
                _telemetry.RecordNodeExecutionDuration(plannedNode.Type, nodeExecutionElapsed);
            }

            if (result is LegacyNodeResult.Success successResult)
            {
                nodeState.Status = NodeStatus.Completed;
                nodeState.Outputs = successResult.Outputs ?? new Dictionary<string, object>();
                nodeState.ErrorMessage = null;

                var retryState = await _dbContext.NodeRetryStates
                    .SingleOrDefaultAsync(
                        state => state.ExecutionInstanceId == instance.Id && state.NodeId == currentNodeId,
                        cancellationToken);

                if (retryState != null)
                {
                    _dbContext.NodeRetryStates.Remove(retryState);
                }

                var completionMessage = $"Node '{currentNodeId}' completed successfully.";
                if (plannedNode.Type.Equals("log", StringComparison.OrdinalIgnoreCase) &&
                    nodeState.Outputs.TryGetValue("result", out var loggedMessage) &&
                    loggedMessage != null)
                {
                    completionMessage = $"[LOG] {(loggedMessage is string s ? s : JsonSerializer.Serialize(loggedMessage))}";
                }

                var journalData = nodeState.Outputs.Count > 0
                    ? new Dictionary<string, object>(nodeState.Outputs)
                    : null;

                if (attemptId.HasValue)
                {
                    journalData ??= new Dictionary<string, object>();
                    journalData["AttemptId"] = attemptId.Value.ToString();
                }

                await PublishJournalEntryAsync(
                    instance,
                    JournalEventTypes.NodeExecutionCompleted,
                    completionMessage,
                    nodeId: currentNodeId,
                    data: journalData,
                    cancellationToken: cancellationToken);

                var outgoingEdges = plan.Edges.Where(edge => edge.From == currentNodeId);
                string? selectedPort = null;
                if ((plannedNode.Type.Equals("condition", StringComparison.OrdinalIgnoreCase) ||
                     plannedNode.Type.Equals("forLoop", StringComparison.OrdinalIgnoreCase) ||
                     plannedNode.Type.Equals("parallelForEach", StringComparison.OrdinalIgnoreCase)) &&
                    nodeState.Outputs.TryGetValue("selectedPort", out var portObj) &&
                    portObj != null)
                {
                    selectedPort = portObj is string portString
                        ? portString
                        : portObj is JsonElement portElement && portElement.ValueKind == JsonValueKind.String
                            ? portElement.GetString()
                            : portObj.ToString();
                }

                foreach (var edge in outgoingEdges)
                {
                    if (selectedPort != null && !edge.Output.Equals(selectedPort, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    scheduledNodes.Enqueue(edge.To);
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            else if (result is LegacyNodeResult.WaitForEvent waitResult)
            {
                nodeState.Status = NodeStatus.Waiting;
                nodeState.Outputs["eventName"] = waitResult.EventName;

                instance.Status = ExecutionStatus.Suspended;
                instance.VariableState = JsonSerializer.Serialize(instance.GlobalVariables);
                instance.UpdatedAt = _timeProvider.GetUtcNow();

                _dbContext.Entry(instance).Property(execution => execution.Status).IsModified = true;
                _dbContext.Entry(instance).Property(execution => execution.VariableState).IsModified = true;

                // Persist first (SaveChanges is atomic), THEN journal. The journal writer uses a separate
                // SQLite connection, which deadlocks against an open EF write-transaction (single writer) —
                // so we must NOT journal inside one (the previous BeginTransaction here caused exactly that
                // in production; it was only masked in tests by a shared connection).
                await _dbContext.SaveChangesAsync(cancellationToken);

                await PublishJournalEntryAsync(
                    instance,
                    JournalEventTypes.WorkflowSuspended,
                    $"Workflow suspended. Node '{currentNodeId}' is waiting for event '{waitResult.EventName}'.",
                    nodeId: currentNodeId,
                    data: new Dictionary<string, object>
                    {
                        ["SuspendedNodeId"] = currentNodeId,
                        ["Variables"] = instance.GlobalVariables,
                        ["_v"] = "v2"
                    },
                    cancellationToken: cancellationToken);

                return;
            }
            else if (result is LegacyNodeResult.Delay delayResult)
            {
                // Non-blocking delay: park the node and schedule a TIMED resume (a Resume work item with
                // NotBeforeUtc), then free the worker. ProcessPendingWorkItemsAsync picks it up when due and
                // resumes via the same node-id path as WaitForEvent. So other queued runs aren't stalled by
                // this wait — delays overlap instead of accumulating.
                //
                // NOTE: no explicit BeginTransaction here. SaveChangesAsync is already atomic, and the
                // journal writer uses a SEPARATE SQLite connection — INSERTing from it while an EF write
                // transaction is open deadlocks SQLite (single writer). So we persist first, then journal.
                var resumeAtUtc = _timeProvider.GetUtcNow().AddMilliseconds(delayResult.DurationMs);

                nodeState.Status = NodeStatus.Waiting;

                instance.Status = ExecutionStatus.Suspended;
                instance.VariableState = JsonSerializer.Serialize(instance.GlobalVariables);
                instance.UpdatedAt = _timeProvider.GetUtcNow();
                _dbContext.Entry(instance).Property(execution => execution.Status).IsModified = true;
                _dbContext.Entry(instance).Property(execution => execution.VariableState).IsModified = true;

                var resumeWorkItem = new ExecutionWorkItem
                {
                    Id = Guid.NewGuid(),
                    ExecutionInstanceId = instance.Id,
                    Type = "Resume",
                    Payload = JsonSerializer.Serialize(new
                    {
                        nodeId = currentNodeId.Value,
                        workflowVersionId = instance.WorkflowVersionId!.Value.Value,
                        output = new Dictionary<string, object>(),
                    }),
                    Status = WorkItemStatus.Pending,
                    CreatedAtUtc = _timeProvider.GetUtcNow(),
                    NotBeforeUtc = resumeAtUtc,
                    ProcessedAtUtc = null,
                };
                await _dbContext.ExecutionWorkItems.AddAsync(resumeWorkItem, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);

                // Journal AFTER the save (no open transaction) so the resume can rehydrate globals — same
                // `["Variables"]` shape as WaitForEvent — without the journal-writer connection deadlocking.
                await PublishJournalEntryAsync(
                    instance,
                    JournalEventTypes.WorkflowSuspended,
                    $"Workflow suspended. Node '{currentNodeId}' is delaying until {resumeAtUtc:o}.",
                    nodeId: currentNodeId,
                    data: new Dictionary<string, object>
                    {
                        ["SuspendedNodeId"] = currentNodeId,
                        ["Variables"] = instance.GlobalVariables,
                        ["ResumeAtUtc"] = resumeAtUtc,
                        ["Reason"] = "delay",
                        ["_v"] = "v2"
                    },
                    cancellationToken: cancellationToken);

                return;
            }
            else if (result is LegacyNodeResult.Failure failureResult)
            {
                nodeState.Status = NodeStatus.Failed;
                nodeState.ErrorMessage = failureResult.ErrorMessage;

                await PublishJournalEntryAsync(
                    instance,
                    JournalEventTypes.NodeExecutionFailed,
                    $"Node '{currentNodeId}' failed: {failureResult.ErrorMessage}.",
                    nodeId: currentNodeId,
                    data: CreateFailureJournalData(failureResult.ErrorMessage, attemptId, failureResult.ErrorCode),
                    cancellationToken: cancellationToken);

                await HandleNodeFailureAsync(instance, plan, nodeState, scheduledNodes, cancellationToken);
            }
        }
    }

    private void ResetLoopBodyNodes(
        ExecutionInstance instance,
        ExecutionPlan plan,
        NodeId loopNodeId,
        HashSet<NodeId> visitedInThisSession)
    {
        // Find the edge that carries the iteration signal out of the loop node.
        // Prefer an edge explicitly named "start"; fall back to any outgoing edge
        // that is NOT the exit/success path (i.e. not named "success").
        var startEdge =
            plan.Edges.FirstOrDefault(e => e.From == loopNodeId &&
                e.Output.Equals("start", StringComparison.OrdinalIgnoreCase)) ??
            plan.Edges.FirstOrDefault(e => e.From == loopNodeId &&
                !e.Output.Equals("success", StringComparison.OrdinalIgnoreCase) &&
                !e.Output.Equals("failure", StringComparison.OrdinalIgnoreCase) &&
                !e.Output.Equals("error", StringComparison.OrdinalIgnoreCase));

        if (startEdge == null) return;

        // Collect all body nodes: follow edges forward from the first body node,
        // but stop at the loop node itself AND at any node that is only reachable
        // via the loop's non-iteration outputs (those nodes belong to post-loop flow).
        var exitTargets = plan.Edges
            .Where(e => e.From == loopNodeId &&
                (e.Output.Equals("success", StringComparison.OrdinalIgnoreCase) ||
                 e.Output.Equals("failure", StringComparison.OrdinalIgnoreCase) ||
                 e.Output.Equals("error", StringComparison.OrdinalIgnoreCase)))
            .Select(e => e.To)
            .ToHashSet();

        var bodyNodes = new HashSet<NodeId>();
        var queue = new Queue<NodeId>();
        queue.Enqueue(startEdge.To);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == loopNodeId || exitTargets.Contains(current)) continue;

            if (bodyNodes.Add(current))
            {
                var outgoing = plan.Edges.Where(e => e.From == current);
                foreach (var edge in outgoing)
                {
                    if (edge.To != loopNodeId && !exitTargets.Contains(edge.To))
                    {
                        queue.Enqueue(edge.To);
                    }
                }
            }
        }

        foreach (var nodeId in bodyNodes)
        {
            var state = instance.NodeStates.FirstOrDefault(s => s.NodeId == nodeId);
            if (state != null)
            {
                state.Status = NodeStatus.Pending;
            }
            visitedInThisSession.Remove(nodeId);
        }
    }

    /// <summary>
    /// Executes a <c>parallelForEach</c> node as a true parallel container: it runs the body
    /// <em>subgraph</em> (everything reachable from its <c>start</c> output, up to the loop-back
    /// <c>end</c> input) once per collection item, concurrently and bounded by <c>maxParallelism</c>.
    /// <para>
    /// Each item runs in its own in-memory sub-executor — a private node-state map and a private copy
    /// of the global variables — so concurrent iterations never share mutable state, and no iteration
    /// touches the (non-thread-safe) DbContext. The per-item result is the value the body sends back
    /// into the loop's <c>end</c> input (mirroring ForLoop). Only after <see cref="Task.WhenAll(Task[])"/>
    /// does this thread commit the aggregate and per-body-node states.
    /// </para>
    /// <para>
    /// Limitations (v1): the body may not contain nested control-flow nodes (forLoop / parallelForEach /
    /// join) — wrap such logic in a subflow. A body node returning WaitForEvent fails that item, since a
    /// single parallel branch cannot suspend the whole run. Only <c>condition</c> port-selection is honored
    /// inside the body, matching the main executor.
    /// </para>
    /// </summary>
    private async Task ExecuteParallelForEachAsync(
        ExecutionInstance instance,
        ExecutionPlan plan,
        PlannedNode plannedNode,
        NodeState nodeState,
        Dictionary<string, object> evaluatedInputs,
        Queue<NodeId> scheduledNodes,
        CancellationToken cancellationToken)
    {
        var nodeId = nodeState.NodeId;

        var maxParallelism = 8;
        if (evaluatedInputs.TryGetValue("maxParallelism", out var maxObj) && maxObj != null &&
            int.TryParse(maxObj.ToString(), out var parsedMax))
        {
            maxParallelism = Math.Clamp(parsedMax, 1, 64);
        }

        var continueOnError = false;
        if (evaluatedInputs.TryGetValue("continueOnError", out var coeObj) && coeObj != null &&
            bool.TryParse(coeObj.ToString(), out var parsedCoe))
        {
            continueOnError = parsedCoe;
        }

        var items = MaterializeCollection(evaluatedInputs.TryGetValue("collection", out var colObj) ? colObj : null);

        // The body subgraph starts at the 'start' output and rejoins at the 'end' input (same
        // convention as ForLoop). Both support fan-out / fan-in: 'start' may launch several body
        // branches and 'end' may collect several converging branches. 'success'/'failure'/'error'
        // edges are the POST-loop continuation and bound the body so the BFS never wanders out.
        var startTargets = plan.Edges
            .Where(edge => edge.From == nodeId && edge.Output.Equals("start", StringComparison.OrdinalIgnoreCase))
            .Select(edge => edge.To)
            .ToHashSet();
        var endEdges = plan.Edges
            .Where(edge => edge.To == nodeId && edge.Input.Equals("end", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var exitTargets = plan.Edges
            .Where(edge => edge.From == nodeId &&
                (edge.Output.Equals("success", StringComparison.OrdinalIgnoreCase) ||
                 edge.Output.Equals("failure", StringComparison.OrdinalIgnoreCase) ||
                 edge.Output.Equals("error", StringComparison.OrdinalIgnoreCase)))
            .Select(edge => edge.To)
            .ToHashSet();

        // Collect the body subgraph (everything reachable from 'start', minus the loop node and the
        // post-loop nodes), then pre-resolve every body type's task + timeout on THIS thread so the
        // concurrent iterations never call into the registry / manifest provider (which can hit the DB).
        var bodyNodeById = new Dictionary<NodeId, PlannedNode>();
        var bodyTasks = new Dictionary<string, INodeTask>(StringComparer.OrdinalIgnoreCase);
        var bodyTimeouts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bodyNonExpressionParams = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        string? setupError = null;

        if (startTargets.Count == 0)
        {
            setupError = "parallelForEach requires a body connected to its 'start' output.";
        }
        else
        {
            var bfs = new Queue<NodeId>();
            foreach (var target in startTargets)
            {
                bfs.Enqueue(target);
            }
            var seen = new HashSet<NodeId>();
            while (bfs.Count > 0)
            {
                var current = bfs.Dequeue();
                if (current == nodeId || exitTargets.Contains(current) || !seen.Add(current))
                {
                    continue;
                }
                var planned = plan.Nodes.FirstOrDefault(node => node.Id == current);
                if (planned == null)
                {
                    continue;
                }
                bodyNodeById[current] = planned;
                foreach (var edge in plan.Edges.Where(edge => edge.From == current))
                {
                    if (edge.To != nodeId && !exitTargets.Contains(edge.To))
                    {
                        bfs.Enqueue(edge.To);
                    }
                }
            }

            if (bodyNodeById.Count == 0)
            {
                setupError = "parallelForEach body is empty (nothing wired to its 'start' output).";
            }
        }

        if (setupError == null)
        {
            foreach (var planned in bodyNodeById.Values)
            {
                if (planned.Type.Equals("forLoop", StringComparison.OrdinalIgnoreCase) ||
                    planned.Type.Equals("parallelForEach", StringComparison.OrdinalIgnoreCase) ||
                    planned.Type.Equals("join", StringComparison.OrdinalIgnoreCase))
                {
                    setupError =
                        $"parallelForEach body cannot contain a nested control-flow node " +
                        $"('{planned.Id}' of type '{planned.Type}'). Move that logic into a subflow.";
                    break;
                }

                if (bodyTasks.ContainsKey(planned.Type))
                {
                    continue;
                }

                var resolvedTask = _registry.GetTask(planned.Type);
                if (resolvedTask == null)
                {
                    setupError = $"parallelForEach body node '{planned.Id}' has no registered task for type '{planned.Type}'.";
                    break;
                }
                bodyTasks[planned.Type] = resolvedTask;

                var manifest = await GetManifestAsync(planned.Type, cancellationToken);
                bodyTimeouts[planned.Type] = manifest != null && manifest.DefaultTimeoutSeconds > 0
                    ? Math.Clamp(manifest.DefaultTimeoutSeconds, 1, 600)
                    : 30;
                bodyNonExpressionParams[planned.Type] = NonExpressionParams(manifest);
            }
        }

        if (setupError != null)
        {
            nodeState.Status = NodeStatus.Failed;
            nodeState.ErrorMessage = setupError;

            await PublishJournalEntryAsync(
                instance,
                JournalEventTypes.NodeExecutionFailed,
                $"Node '{nodeId}' failed: {setupError}",
                nodeId: nodeId,
                data: CreateFailureJournalData(setupError, attemptId: null),
                cancellationToken: cancellationToken);

            await HandleNodeFailureAsync(instance, plan, nodeState, scheduledNodes, cancellationToken);
            return;
        }

        var globalsSnapshot = new Dictionary<string, object>(instance.GlobalVariables);

        var results = new object?[items.Count];
        var itemErrors = new string?[items.Count];
        var iterationOutputs = new Dictionary<NodeId, Dictionary<string, object>>?[items.Count];

        using (var throttle = new SemaphoreSlim(maxParallelism))
        {
            // Runs the whole body subgraph for one item in an isolated, in-memory mini-executor.
            // No DbContext, no journal, no shared NodeState — everything lives in localOutputs/localGlobals.
            async Task<(object? Result, string? Error, Dictionary<NodeId, Dictionary<string, object>> Outputs)> RunBodyAsync(object? item, int index)
            {
                var localGlobals = new Dictionary<string, object>(globalsSnapshot);
                var localOutputs = new Dictionary<NodeId, Dictionary<string, object>>();
                var visited = new HashSet<NodeId>();
                var localQueue = new Queue<NodeId>();
                foreach (var target in startTargets)
                {
                    localQueue.Enqueue(target);
                }

                while (localQueue.Count > 0)
                {
                    var currentId = localQueue.Dequeue();
                    if (currentId == nodeId || exitTargets.Contains(currentId) || !visited.Add(currentId))
                    {
                        continue;
                    }
                    if (!bodyNodeById.TryGetValue(currentId, out var planned))
                    {
                        continue;
                    }

                    var inputs = new Dictionary<string, object>(planned.Properties, StringComparer.OrdinalIgnoreCase);
                    foreach (var edge in plan.Edges.Where(edge => edge.To == currentId))
                    {
                        if (edge.From == nodeId && edge.Output.Equals("start", StringComparison.OrdinalIgnoreCase))
                        {
                            inputs[edge.Input] = item!;
                        }
                        else if (localOutputs.TryGetValue(edge.From, out var bodyOutputs) &&
                                 bodyOutputs.TryGetValue(edge.Output, out var bodyValue))
                        {
                            inputs[edge.Input] = bodyValue;
                        }
                        else
                        {
                            // A pre-loop predecessor: read its already-completed output (read-only).
                            var predecessor = instance.NodeStates.FirstOrDefault(state => state.NodeId == edge.From);
                            if (predecessor != null && predecessor.Status == NodeStatus.Completed &&
                                predecessor.Outputs.TryGetValue(edge.Output, out var predValue))
                            {
                                inputs[edge.Input] = predValue;
                            }
                        }
                    }

                    if (startTargets.Contains(currentId))
                    {
                        inputs["item"] = item!;
                        inputs["index"] = index;
                    }

                    var iterationState = new IterationState(localGlobals, localOutputs, instance);
                    var nonExpr = bodyNonExpressionParams.TryGetValue(planned.Type, out var ne)
                        ? ne
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var evaluated = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in inputs)
                    {
                        bool resolve = !nonExpr.Contains(kvp.Key);
                        evaluated[kvp.Key] = EvaluatePropertyValue(kvp.Value, iterationState, resolve)!;
                    }

                    var task = bodyTasks[planned.Type];
                    var timeoutSeconds = bodyTimeouts.TryGetValue(planned.Type, out var to) ? to : 30;
                    var context = new NodeExecutionContext(
                        WorkflowId: instance.WorkflowDefinitionId,
                        ExecutionId: instance.Id.Value,
                        NodeId: currentId,
                        Inputs: evaluated,
                        GlobalVariables: localGlobals,
                        State: iterationState);

                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                    LegacyNodeResult itemResult;
                    try
                    {
                        itemResult = await task.ExecuteAsync(context, linkedCts.Token);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        return (null, $"Item {index}: node '{currentId}' timed out after {timeoutSeconds}s.", localOutputs);
                    }
                    catch (Exception ex)
                    {
                        return (null, $"Item {index}: node '{currentId}': {ex.Message}", localOutputs);
                    }

                    // A parallel branch can't suspend the whole run, so a Delay here is honored INLINE
                    // (bounded by the body timeout) rather than parking the execution.
                    if (itemResult is LegacyNodeResult.Delay bodyDelay)
                    {
                        try
                        {
                            await Task.Delay(bodyDelay.DurationMs, linkedCts.Token);
                            itemResult = new LegacyNodeResult.Success();
                        }
                        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            return (null, $"Item {index}: node '{currentId}' timed out after {timeoutSeconds}s.", localOutputs);
                        }
                    }

                    if (itemResult is LegacyNodeResult.Failure failure)
                    {
                        return (null, $"Item {index}: node '{currentId}': {failure.ErrorMessage}", localOutputs);
                    }
                    if (itemResult is not LegacyNodeResult.Success success)
                    {
                        return (null, $"Item {index}: node '{currentId}' returned an unsupported result " +
                            "(WaitForEvent is not allowed inside a parallel body).", localOutputs);
                    }

                    var outputs = success.Outputs ?? new Dictionary<string, object>();
                    localOutputs[currentId] = outputs;

                    string? selectedPort = null;
                    if (planned.Type.Equals("condition", StringComparison.OrdinalIgnoreCase) &&
                        outputs.TryGetValue("selectedPort", out var portObj) && portObj != null)
                    {
                        selectedPort = portObj as string ?? portObj.ToString();
                    }

                    foreach (var edge in plan.Edges.Where(edge => edge.From == currentId))
                    {
                        if (selectedPort != null && !edge.Output.Equals(selectedPort, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (edge.To == nodeId || exitTargets.Contains(edge.To))
                        {
                            continue;
                        }
                        localQueue.Enqueue(edge.To);
                    }
                }

                // Per-item result = the value(s) the body fed back into the loop's 'end' input. One
                // end branch yields a scalar; several converging branches (fan-in / join) yield a list.
                var endValues = new List<object?>();
                foreach (var edge in endEdges)
                {
                    if (localOutputs.TryGetValue(edge.From, out var endOutputs))
                    {
                        endValues.Add(endOutputs.TryGetValue(edge.Output, out var endValue) ? endValue : endOutputs);
                    }
                }
                object? result = endValues.Count == 1 ? endValues[0]
                    : endValues.Count > 1 ? endValues
                    : null;
                return (result, null, localOutputs);
            }

            async Task RunItemAsync(object? item, int index)
            {
                await throttle.WaitAsync(cancellationToken);
                try
                {
                    var (result, error, outputs) = await RunBodyAsync(item, index);
                    results[index] = result;
                    itemErrors[index] = error;
                    iterationOutputs[index] = outputs;
                }
                finally
                {
                    throttle.Release();
                }
            }

            var itemTasks = new List<Task>(items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                itemTasks.Add(RunItemAsync(items[i], i));
            }

            await Task.WhenAll(itemTasks);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var failedIndexes = new List<int>();
        for (var i = 0; i < itemErrors.Length; i++)
        {
            if (itemErrors[i] != null)
            {
                failedIndexes.Add(i);
                if (continueOnError)
                {
                    results[i] = new Dictionary<string, object> { ["error"] = itemErrors[i]! };
                }
            }
        }

        if (failedIndexes.Count > 0 && !continueOnError)
        {
            var firstError = itemErrors[failedIndexes[0]];
            nodeState.Status = NodeStatus.Failed;
            nodeState.ErrorMessage =
                $"{failedIndexes.Count} of {items.Count} parallel item(s) failed. First error: {firstError}";

            await PublishJournalEntryAsync(
                instance,
                JournalEventTypes.NodeExecutionFailed,
                $"Node '{nodeId}' failed: {nodeState.ErrorMessage}",
                nodeId: nodeId,
                data: CreateFailureJournalData(nodeState.ErrorMessage, attemptId: null),
                cancellationToken: cancellationToken);

            await HandleNodeFailureAsync(instance, plan, nodeState, scheduledNodes, cancellationToken);
            return;
        }

        // Best-effort observability: surface the body nodes' last-iteration outputs as completed
        // NodeStates so the inspector/journal shows the body ran. Per-iteration states are not
        // individually persisted (an N-item run would otherwise explode the state table).
        var lastIterationOutputs = iterationOutputs.LastOrDefault(outputs => outputs != null);
        if (lastIterationOutputs != null)
        {
            foreach (var (bodyNodeId, bodyOutputs) in lastIterationOutputs)
            {
                var bodyState = instance.NodeStates.FirstOrDefault(state => state.NodeId == bodyNodeId);
                if (bodyState == null)
                {
                    bodyState = new NodeState
                    {
                        Id = Guid.NewGuid(),
                        ExecutionInstanceId = instance.Id,
                        NodeId = bodyNodeId,
                        Status = NodeStatus.Pending,
                        ExecutionCount = 0
                    };
                    instance.NodeStates.Add(bodyState);
                }
                bodyState.Status = NodeStatus.Completed;
                bodyState.Outputs = new Dictionary<string, object>(bodyOutputs);
                bodyState.ExecutionCount = items.Count;
            }
        }

        var resultsList = results.ToList();
        nodeState.Status = NodeStatus.Completed;
        nodeState.ErrorMessage = null;
        nodeState.Outputs = new Dictionary<string, object>
        {
            ["results"] = resultsList,
            ["count"] = items.Count,
            ["failedCount"] = failedIndexes.Count,
            ["selectedPort"] = "success"
        };

        await PublishJournalEntryAsync(
            instance,
            JournalEventTypes.NodeExecutionCompleted,
            $"Node '{nodeId}' processed {items.Count} item(s) with up to {maxParallelism} in parallel" +
            (failedIndexes.Count > 0 ? $" ({failedIndexes.Count} failed, continued)." : "."),
            nodeId: nodeId,
            data: new Dictionary<string, object>(nodeState.Outputs),
            cancellationToken: cancellationToken);

        foreach (var edge in plan.Edges.Where(edge =>
                     edge.From == nodeId && edge.Output.Equals("success", StringComparison.OrdinalIgnoreCase)))
        {
            scheduledNodes.Enqueue(edge.To);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Normalizes a <c>collection</c> input (JSON string, JSON array, enumerable, or scalar) into a
    /// concrete list of items. Mirrors the parsing the ForLoop node performs for its <c>foreach</c> mode.
    /// </summary>
    private static List<object?> MaterializeCollection(object? collection)
    {
        var items = new List<object?>();
        if (collection == null)
        {
            return items;
        }

        switch (collection)
        {
            case string text:
                var trimmed = text.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    try
                    {
                        return JsonSerializer.Deserialize<List<object?>>(trimmed) ?? new List<object?>();
                    }
                    catch
                    {
                        // Fall back to comma-separated values.
                    }
                }
                foreach (var part in text.Split(','))
                {
                    items.Add(part.Trim());
                }
                return items;

            case JsonElement element when element.ValueKind == JsonValueKind.Array:
                return JsonSerializer.Deserialize<List<object?>>(element.GetRawText()) ?? new List<object?>();

            case System.Collections.IEnumerable enumerable:
                foreach (var entry in enumerable)
                {
                    items.Add(entry);
                }
                return items;

            default:
                items.Add(collection);
                return items;
        }
    }

    private async Task HandleNodeFailureAsync(
        ExecutionInstance instance,
        ExecutionPlan plan,
        NodeState nodeState,
        Queue<NodeId> scheduledNodes,
        CancellationToken cancellationToken)
    {
        var plannedNode = plan.Nodes.FirstOrDefault(node => node.Id == nodeState.NodeId);
        if (plannedNode != null)
        {
            var manifest = await GetManifestAsync(plannedNode.Type, cancellationToken);
            if (manifest != null && await TryScheduleRetryAsync(instance, nodeState, manifest, cancellationToken))
            {
                scheduledNodes.Clear();
                return;
            }
        }

        await ClearRetryStateAsync(instance.Id, nodeState.NodeId, cancellationToken);

        var failureEdges = plan.Edges.Where(e => e.From == nodeState.NodeId &&
            (e.Output.Equals("failure", StringComparison.OrdinalIgnoreCase) || e.Output.Equals("error", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (failureEdges.Count > 0)
        {
            foreach (var edge in failureEdges)
            {
                scheduledNodes.Enqueue(edge.To);
            }
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            instance.Status = ExecutionStatus.Failed;
            instance.UpdatedAt = DateTimeOffset.UtcNow;

            await PublishJournalEntryAsync(instance, JournalEventTypes.WorkflowFailed, $"Workflow execution failed at node '{nodeState.NodeId}'.", cancellationToken: cancellationToken);

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<bool> IsExecutionCancelledAsync(ExecutionInstanceId executionId, CancellationToken cancellationToken)
    {
        // Projection query: bypasses the change tracker so we observe the live database value
        // written by a concurrent deactivate request rather than our in-memory tracked entity.
        var status = await _dbContext.ExecutionInstances
            .Where(execution => execution.Id == executionId)
            .Select(execution => execution.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return status == ExecutionStatus.Cancelled;
    }

    private async Task MarkExecutionCancelledAsync(ExecutionInstance instance, CancellationToken cancellationToken)
    {
        instance.Status = ExecutionStatus.Cancelled;
        instance.UpdatedAt = _timeProvider.GetUtcNow();

        await PublishJournalEntryAsync(
            instance,
            "WorkflowCancelled",
            "Workflow execution was cancelled because its workflow was deactivated.",
            cancellationToken: cancellationToken);

        _telemetry.RecordExecutionStopped();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private Dictionary<string, object>? TryGetMockedSideEffectOutputs(NodeId nodeId, NodePackageManifest? manifest)
    {
        if (!_mockSideEffects ||
            _mockSourceOutputs == null ||
            manifest?.SideEffectKind != NodeSideEffectKind.NonIdempotentSideEffect)
        {
            return null;
        }

        if (!_mockSourceOutputs.TryGetValue(nodeId, out var sourceOutputs) || sourceOutputs.Count == 0)
        {
            return null;
        }

        return new Dictionary<string, object>(sourceOutputs, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<NodePackageManifest?> GetManifestAsync(string nodeType, CancellationToken cancellationToken)
    {
        var manifest = await _compiler.ManifestProvider.GetManifestAsync(new NodePackageId(nodeType), cancellationToken);
        if (manifest == null)
        {
            return null;
        }

        return manifest with
        {
            SideEffectKind = manifest.SideEffectKind ?? NodeSideEffectKind.NonIdempotentSideEffect,
            RetryPolicy = manifest.RetryPolicy ?? new RetryPolicy()
        };
    }

    private async Task<ImmutableArray<NodeId>> ResolveEntryNodesForTriggerOriginAsync(
        ExecutionPlan plan,
        ExecutionInstance instance,
        CancellationToken cancellationToken)
    {
        var triggerOrigin = instance.TriggerOrigin;

        // A device-event run carries the explicit entry nodes (the fired event pin's downstream nodes);
        // begin there rather than at a compiled trigger so the device event drives exactly that wire.
        if (triggerOrigin.Equals(ExternalSignalRunEnqueuer.DeviceEventTriggerOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return ResolveDeviceEventEntryNodes(plan, instance);
        }

        var matchingEntryNodes = new List<NodeId>();
        foreach (var entryNodeId in plan.EntryNodes)
        {
            var plannedNode = plan.Nodes.FirstOrDefault(node => node.Id == entryNodeId);
            if (plannedNode is null)
            {
                continue;
            }

            var manifest = await GetManifestAsync(plannedNode.Type, cancellationToken);
            if (manifest?.TriggerOnly != true)
            {
                continue;
            }

            if (IsTriggerCompatibleWithOrigin(plannedNode.Type, triggerOrigin))
            {
                matchingEntryNodes.Add(entryNodeId);
            }
        }

        return matchingEntryNodes.Count > 0 ? matchingEntryNodes.ToImmutableArray() : plan.EntryNodes;
    }

    /// <summary>
    /// Entry nodes for a device-event run: the explicit ids carried in globals (the fired event pin's
    /// downstream nodes), kept only if they exist in the plan. No fallback to <c>plan.EntryNodes</c> —
    /// an empty/stale set must start nothing, not run unrelated triggers.
    /// </summary>
    private static ImmutableArray<NodeId> ResolveDeviceEventEntryNodes(ExecutionPlan plan, ExecutionInstance instance)
    {
        if (instance.GlobalVariables is null
            || !instance.GlobalVariables.TryGetValue(ExternalSignalRunEnqueuer.EntryNodesVariableKey, out var raw)
            || raw is null)
        {
            return ImmutableArray<NodeId>.Empty;
        }

        var planNodeIds = plan.Nodes.Select(n => n.Id.Value).ToHashSet(StringComparer.Ordinal);
        var entryNodes = new List<NodeId>();
        foreach (var id in EnumerateEntryNodeIds(raw))
        {
            if (planNodeIds.Contains(id))
            {
                entryNodes.Add(NodeId.Create(id));
            }
        }
        return entryNodes.ToImmutableArray();
    }

    // Globals round-trip through JSON, so the stored List<string> comes back as a JsonElement array on
    // reload; accept both that and the in-memory list/enumerable shape.
    private static IEnumerable<string> EnumerateEntryNodeIds(object raw)
    {
        switch (raw)
        {
            case JsonElement je when je.ValueKind == JsonValueKind.Array:
                foreach (var el in je.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var s = el.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) yield return s!;
                    }
                }
                break;
            case IEnumerable<string> strings:
                foreach (var s in strings)
                {
                    if (!string.IsNullOrWhiteSpace(s)) yield return s;
                }
                break;
            case System.Collections.IEnumerable seq and not string:
                foreach (var item in seq)
                {
                    var s = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) yield return s!;
                }
                break;
        }
    }

    private async Task CompleteTriggerNodeAsync(
        ExecutionInstance instance,
        ExecutionPlan plan,
        NodeState nodeState,
        PlannedNode plannedNode,
        Queue<NodeId> scheduledNodes,
        CancellationToken cancellationToken)
    {
        nodeState.Inputs = new Dictionary<string, object>(plannedNode.Properties, StringComparer.OrdinalIgnoreCase);
        nodeState.Status = NodeStatus.Completed;
        nodeState.ExecutionCount++;
        nodeState.ErrorMessage = null;
        nodeState.Outputs = CreateTriggerOutputs(plannedNode.Type, instance);
        instance.UpdatedAt = _timeProvider.GetUtcNow();

        await PublishJournalEntryAsync(
            instance,
            JournalEventTypes.NodeExecutionCompleted,
            $"Trigger node '{plannedNode.Id.Value}' activated.",
            nodeId: plannedNode.Id,
            data: nodeState.Outputs.Count > 0 ? new Dictionary<string, object>(nodeState.Outputs) : null,
            cancellationToken: cancellationToken);

        foreach (var edge in plan.Edges.Where(edge => edge.From == plannedNode.Id))
        {
            scheduledNodes.Enqueue(edge.To);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static Dictionary<string, object> CreateTriggerOutputs(string nodeType, ExecutionInstance instance)
    {
        var outputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (nodeType.Equals("scheduler", StringComparison.OrdinalIgnoreCase))
        {
            outputs["triggeredAt"] = instance.CreatedAt;
        }
        else if (nodeType.Equals("pollingTrigger", StringComparison.OrdinalIgnoreCase))
        {
            if (instance.GlobalVariables is not null &&
                instance.GlobalVariables.TryGetValue(TriggerPayloadKeys.Poll, out var payload) &&
                payload is not null)
            {
                outputs["result"] = payload;
            }
        }
        else if (nodeType.Equals("errorTrigger", StringComparison.OrdinalIgnoreCase))
        {
            if (instance.GlobalVariables is not null)
            {
                // The whole failure context on `result`, plus each field on its own output so it can be
                // promoted to a draggable variable in the editor (resolves via nodeState.Outputs[field]).
                if (instance.GlobalVariables.TryGetValue(TriggerPayloadKeys.Error, out var payload) &&
                    payload is not null)
                {
                    outputs["result"] = payload;
                }

                foreach (var key in ErrorWorkflowRunEnqueuer.FieldKeys)
                {
                    if (instance.GlobalVariables.TryGetValue(key, out var fieldValue) && fieldValue is not null)
                    {
                        outputs[key] = fieldValue;
                    }
                }
            }
        }

        return outputs;
    }

    private static bool IsTriggerCompatibleWithOrigin(string nodeType, string triggerOrigin)
    {
        if (triggerOrigin.Equals("schedule", StringComparison.OrdinalIgnoreCase))
        {
            return nodeType.Equals("scheduler", StringComparison.OrdinalIgnoreCase);
        }

        if (triggerOrigin.Equals("webhook", StringComparison.OrdinalIgnoreCase))
        {
            return nodeType.Equals("webhookTrigger", StringComparison.OrdinalIgnoreCase);
        }

        if (triggerOrigin.Equals("poll", StringComparison.OrdinalIgnoreCase))
        {
            return nodeType.Equals("pollingTrigger", StringComparison.OrdinalIgnoreCase);
        }

        if (triggerOrigin.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            return nodeType.Equals("errorTrigger", StringComparison.OrdinalIgnoreCase);
        }

        return nodeType.Equals("start", StringComparison.OrdinalIgnoreCase)
            || nodeType.Equals("manualTrigger", StringComparison.OrdinalIgnoreCase);
    }

}
