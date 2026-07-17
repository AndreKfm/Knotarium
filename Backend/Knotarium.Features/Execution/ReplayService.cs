// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Features.Execution;

/// <summary>
/// Payload for a <c>Replay</c> execution work item. Public so both <see cref="ReplayService"/>
/// (which enqueues it) and the work-item handler (which consumes it) can share the shape.
/// </summary>
public sealed record ReplayWorkItemPayload(string? NodeId, Guid? WorkflowVersionId, bool MockSideEffects);

/// <summary>A downstream node that will re-execute a non-idempotent side effect during replay.</summary>
public sealed record ReplayWarning(string NodeId, string SideEffectKind);

/// <summary>The outcome of <see cref="ReplayService.CreateReplayAsync"/>.</summary>
public sealed record ReplayResult(ExecutionInstanceId NewExecutionId, IReadOnlyList<ReplayWarning> Warnings);

/// <summary>Thrown when a replay request is structurally invalid (maps to HTTP 400).</summary>
public sealed class ReplayValidationException : Exception
{
    public ReplayValidationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Builds a replay run from a finished execution: a brand-new <see cref="ExecutionInstance"/>
/// that inherits the source run's upstream node states (historical inputs) and re-executes the
/// cut-point node and its forward closure. The source run is never mutated.
/// </summary>
public sealed class ReplayService
{
    private readonly AppDbContext _dbContext;
    private readonly WorkflowCompiler _compiler;
    private readonly TimeProvider _timeProvider;

    public ReplayService(
        AppDbContext dbContext,
        WorkflowCompiler compiler,
        TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext;
        _compiler = compiler;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <returns>The replay result, or <c>null</c> when the source execution does not exist.</returns>
    public async Task<ReplayResult?> CreateReplayAsync(
        ExecutionInstanceId sourceExecutionId,
        NodeId fromNodeId,
        WorkflowVersionId? targetVersionId = null,
        bool mockSideEffects = false,
        CancellationToken cancellationToken = default)
    {
        // 1. Load the source run with its persisted node states (the seed material).
        var source = await _dbContext.ExecutionInstances
            .Include(execution => execution.NodeStates)
            .FirstOrDefaultAsync(execution => execution.Id == sourceExecutionId, cancellationToken);

        if (source is null)
        {
            return null;
        }

        // 2. Resolve the target version: explicit override, else the source's own version.
        var versionId = targetVersionId ?? source.WorkflowVersionId
            ?? throw new ReplayValidationException(
                $"Execution '{sourceExecutionId.Value}' has no workflow version to replay against.");

        var workflowVersion = await _dbContext.WorkflowVersions
            .FirstOrDefaultAsync(version => version.Id == versionId, cancellationToken)
            ?? throw new ReplayValidationException($"Workflow version '{versionId.Value}' was not found.");

        var workflow = await _dbContext.WorkflowDefinitions
            .FirstOrDefaultAsync(definition => definition.Id == source.WorkflowDefinitionId, cancellationToken);

        var definition = new WorkflowDefinition(
            source.WorkflowDefinitionId,
            workflow?.Name ?? source.WorkflowDefinitionId.Value,
            workflowVersion.Nodes,
            workflowVersion.Edges);

        var compilation = await _compiler.CompileAsync(definition, cancellationToken);
        if (!compilation.IsSuccess || compilation.Plan is null)
        {
            throw new ReplayValidationException("Target workflow version failed compilation and cannot be replayed.");
        }

        var plan = compilation.Plan;
        if (!plan.Nodes.Any(node => node.Id == fromNodeId))
        {
            throw new ReplayValidationException(
                $"Node '{fromNodeId.Value}' does not exist in the target workflow version.");
        }

        // 3 + 4. Compute the reset/seed split from the plan and the source node states.
        var replayPlan = ReplayPlanCalculator.Compute(plan, fromNodeId, source.NodeStates);

        // Cut-point variable state: the exact GlobalVariables snapshot taken when the source
        // node started. O(1) restore — no journal folding.
        var cutPointState = source.NodeStates.FirstOrDefault(state => state.NodeId == fromNodeId);
        var globalVariables = DeserializeVariables(cutPointState?.VariablesBefore);

        // 5. Create the new, linked execution instance.
        var now = _timeProvider.GetUtcNow();
        var newExecutionId = ExecutionInstanceId.New();
        var replay = new ExecutionInstance
        {
            Id = newExecutionId,
            WorkflowDefinitionId = source.WorkflowDefinitionId,
            WorkflowVersionId = versionId,
            Status = ExecutionStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            TriggerOrigin = "replay",
            GlobalVariables = globalVariables,
            ReplayOfExecutionId = sourceExecutionId,
            ReplayFromNodeId = fromNodeId
        };

        // 6. Clone the seed node states (new identity, same NodeId/Completed/Inputs/Outputs).
        // Reset-set nodes are intentionally absent — the engine creates them fresh on demand.
        foreach (var seed in replayPlan.SeedSet)
        {
            replay.NodeStates.Add(new NodeState
            {
                Id = Guid.NewGuid(),
                ExecutionInstanceId = newExecutionId,
                NodeId = seed.NodeId,
                Status = NodeStatus.Completed,
                Inputs = new Dictionary<string, object>(seed.Inputs, StringComparer.OrdinalIgnoreCase),
                Outputs = new Dictionary<string, object>(seed.Outputs, StringComparer.OrdinalIgnoreCase),
                ExecutionCount = seed.ExecutionCount,
                VariablesBefore = seed.VariablesBefore
            });
        }

        _dbContext.ExecutionInstances.Add(replay);

        // 7. Enqueue the Replay work item.
        var workItem = new ExecutionWorkItem
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = newExecutionId,
            Type = "Replay",
            Payload = JsonSerializer.Serialize(
                new ReplayWorkItemPayload(fromNodeId.Value, versionId.Value, mockSideEffects)),
            NotBeforeUtc = null,
            Status = WorkItemStatus.Pending,
            CreatedAtUtc = now,
            ProcessedAtUtc = null
        };

        await _dbContext.ExecutionWorkItems.AddAsync(workItem, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // 8. Surface non-idempotent downstream effects that will fire for real.
        var warnings = await ComputeWarningsAsync(plan, replayPlan.ResetSet, cancellationToken);

        return new ReplayResult(newExecutionId, warnings);
    }

    private async Task<IReadOnlyList<ReplayWarning>> ComputeWarningsAsync(
        ExecutionPlan plan,
        IReadOnlySet<NodeId> resetSet,
        CancellationToken cancellationToken)
    {
        var warnings = new List<ReplayWarning>();
        foreach (var node in plan.Nodes.Where(node => resetSet.Contains(node.Id)))
        {
            var manifest = await _compiler.ManifestProvider.GetManifestAsync(new NodePackageId(node.Type), cancellationToken);
            if (manifest?.SideEffectKind == NodeSideEffectKind.NonIdempotentSideEffect)
            {
                warnings.Add(new ReplayWarning(node.Id.Value, NodeSideEffectKind.NonIdempotentSideEffect.ToString()));
            }
        }

        return warnings;
    }

    private static Dictionary<string, object> DeserializeVariables(string? variablesBefore)
    {
        if (string.IsNullOrWhiteSpace(variablesBefore))
        {
            return new Dictionary<string, object>();
        }

        return JsonSerializer.Deserialize<Dictionary<string, object>>(variablesBefore, PersistenceJsonOptions.Default)
            ?? new Dictionary<string, object>();
    }
}
