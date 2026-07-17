// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Features.Execution;

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
internal sealed class ParallelForEachNodeRunner
{
    private readonly AppDbContext _dbContext;
    private readonly INodeTaskRegistry _registry;
    private readonly NodeManifestSource _manifests;
    private readonly ExecutionJournalPublisher _journal;

    public ParallelForEachNodeRunner(
        AppDbContext dbContext,
        INodeTaskRegistry registry,
        NodeManifestSource manifests,
        ExecutionJournalPublisher journal)
    {
        _dbContext = dbContext;
        _registry = registry;
        _manifests = manifests;
        _journal = journal;
    }

    /// <summary>
    /// Runs the node to completion. Returns <see langword="true"/> when the node completed (its
    /// successors are enqueued and state saved); <see langword="false"/> when it failed — the node
    /// state and failure journal entry are already written, and the caller owns the failure handling
    /// (retry scheduling / failure edges / failing the run).
    /// </summary>
    public async Task<bool> RunAsync(
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

                var manifest = await _manifests.GetManifestAsync(planned.Type, cancellationToken);
                bodyTimeouts[planned.Type] = manifest != null && manifest.DefaultTimeoutSeconds > 0
                    ? Math.Clamp(manifest.DefaultTimeoutSeconds, 1, 600)
                    : 30;
                bodyNonExpressionParams[planned.Type] = PropertyValueEvaluator.NonExpressionParams(manifest);
            }
        }

        if (setupError != null)
        {
            nodeState.Status = NodeStatus.Failed;
            nodeState.ErrorMessage = setupError;

            await _journal.PublishAsync(
                instance,
                JournalEventTypes.NodeExecutionFailed,
                $"Node '{nodeId}' failed: {setupError}",
                nodeId: nodeId,
                data: ExecutionJournalData.CreateFailureJournalData(setupError, attemptId: null),
                cancellationToken: cancellationToken);

            return false;
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
                        evaluated[kvp.Key] = PropertyValueEvaluator.Evaluate(kvp.Value, iterationState, resolve)!;
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

            await _journal.PublishAsync(
                instance,
                JournalEventTypes.NodeExecutionFailed,
                $"Node '{nodeId}' failed: {nodeState.ErrorMessage}",
                nodeId: nodeId,
                data: ExecutionJournalData.CreateFailureJournalData(nodeState.ErrorMessage, attemptId: null),
                cancellationToken: cancellationToken);

            return false;
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

        await _journal.PublishAsync(
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
        return true;
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

    /// <summary>
    /// State view for a single <c>parallelForEach</c> iteration. Reads variables/outputs from the
    /// iteration's PRIVATE globals + body outputs first, falling back to the (read-only) pre-loop
    /// instance state for upstream node outputs. <see cref="SetVariable"/> writes only to the private
    /// copy, so concurrent iterations never collide and their writes are intentionally not shared.
    /// </summary>
    private sealed class IterationState : IWorkflowState
    {
        private readonly Dictionary<string, object> _globals;
        private readonly Dictionary<NodeId, Dictionary<string, object>> _localOutputs;
        private readonly ExecutionInstance _instance;

        public IterationState(
            Dictionary<string, object> globals,
            Dictionary<NodeId, Dictionary<string, object>> localOutputs,
            ExecutionInstance instance)
        {
            _globals = globals;
            _localOutputs = localOutputs;
            _instance = instance;
        }

        public T? GetVariable<T>(string name)
        {
            if (_globals.TryGetValue(name, out var val))
            {
                return ConvertValue<T>(val);
            }

            // Promoted variable pattern (nodeId_outputHandle): prefer a body node from this iteration,
            // then fall back to a completed pre-loop node on the shared instance.
            var lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                var nodeIdStr = name.Substring(0, lastUnderscore);
                var outputName = name.Substring(lastUnderscore + 1);

                foreach (var kvp in _localOutputs)
                {
                    if (string.Equals(kvp.Key.Value, nodeIdStr, StringComparison.OrdinalIgnoreCase) &&
                        kvp.Value.TryGetValue(outputName, out var localVal))
                    {
                        return ConvertValue<T>(localVal);
                    }
                }

                var nodeState = _instance.NodeStates.FirstOrDefault(ns =>
                    string.Equals(ns.NodeId.Value, nodeIdStr, StringComparison.OrdinalIgnoreCase));
                if (nodeState != null && nodeState.Outputs.TryGetValue(outputName, out var outputVal))
                {
                    return ConvertValue<T>(outputVal);
                }
            }

            return default;
        }

        public bool TryResolveVariable(string name, out object? value)
        {
            // 1. Iteration-private global.
            if (_globals.TryGetValue(name, out var val))
            {
                value = val;
                return true;
            }

            // 2. Promoted node-output: prefer a body node from this iteration, then a pre-loop node.
            var lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                var nodeIdStr = name.Substring(0, lastUnderscore);
                var outputName = name.Substring(lastUnderscore + 1);

                foreach (var kvp in _localOutputs)
                {
                    if (string.Equals(kvp.Key.Value, nodeIdStr, StringComparison.OrdinalIgnoreCase) &&
                        kvp.Value.TryGetValue(outputName, out var localVal))
                    {
                        value = localVal;
                        return true;
                    }
                }

                var nodeState = _instance.NodeStates.FirstOrDefault(ns =>
                    string.Equals(ns.NodeId.Value, nodeIdStr, StringComparison.OrdinalIgnoreCase));
                if (nodeState != null && nodeState.Outputs.TryGetValue(outputName, out var outputVal))
                {
                    value = outputVal;
                    return true;
                }
            }

            value = null;
            return false;
        }

        public void SetVariable(string name, object? value)
        {
            if (value == null)
            {
                _globals.Remove(name);
            }
            else
            {
                _globals[name] = value;
            }
        }

        public JsonElement? GetNodeOutput(NodeId nodeId, string outputName)
        {
            object? value = null;
            if (_localOutputs.TryGetValue(nodeId, out var outputs) && outputs.TryGetValue(outputName, out var localVal))
            {
                value = localVal;
            }
            else
            {
                var nodeState = _instance.NodeStates.FirstOrDefault(ns => ns.NodeId == nodeId);
                if (nodeState != null && nodeState.Outputs.TryGetValue(outputName, out var outputVal))
                {
                    value = outputVal;
                }
            }

            if (value == null)
            {
                return null;
            }
            if (value is JsonElement element)
            {
                return element;
            }
            try
            {
                var json = JsonSerializer.Serialize(value);
                return JsonSerializer.Deserialize<JsonElement>(json);
            }
            catch
            {
                return null;
            }
        }

        private static T? ConvertValue<T>(object? val)
        {
            if (val is T typedVal)
            {
                return typedVal;
            }
            if (val is JsonElement element)
            {
                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return JsonSerializer.Deserialize<T>(element.GetRawText(), options);
                }
                catch
                {
                    return default;
                }
            }
            try
            {
                var converted = Convert.ChangeType(val, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
                return converted != null ? (T)converted : default;
            }
            catch
            {
                return default;
            }
        }
    }
}
