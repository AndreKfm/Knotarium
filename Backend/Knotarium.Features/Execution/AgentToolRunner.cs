// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.Ai;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Knotarium.Features.Execution;

/// <summary>
/// Runs an agent tool (an existing workflow) as an in-process, journaled child run of the parent
/// <c>aiAgent</c> execution, then projects the requested outputs back as the tool result. Implements the
/// Core <see cref="IAgentToolRunner"/> seam so the node never touches execution internals.
///
/// <para>Mirrors <see cref="ErrorWorkflowRunEnqueuer"/> for the seeded-child-instance shape (Pending
/// instance, seeded globals, back-link journal, transaction-then-act), but instead of queueing the child
/// it drives it <b>synchronously in a fresh DI scope</b> via <see cref="WorkflowExecutor.ExecuteAsync"/>.
/// Queueing + awaiting would deadlock under <c>MaxConcurrentRuns = 1</c> (the parent holds the only run
/// slot while the child sits Pending); a direct in-scope execute never touches the worker's run-slot
/// semaphore, so the child's cost is honestly part of the parent's slot.</para>
///
/// <para>Never throws for an ordinary tool failure: a disabled/failed/absent tool is returned as
/// <see cref="AgentToolResult.Success"/> = false so the loop can react. Only genuinely exceptional
/// conditions propagate.</para>
/// </summary>
public sealed class AgentToolRunner : IAgentToolRunner
{
    /// <summary>The <see cref="ExecutionInstance.TriggerOrigin"/> stamped on a tool child run. Unrecognized by
    /// <c>TriggerEntryResolver</c>, so it falls through to the workflow's plain <c>start</c> entry — exactly
    /// what a tool needs. Also the sentinel the recursion guard checks (a run already of this origin may not
    /// spawn further tool runs: depth cap = 1 in v1).</summary>
    public const string ToolTriggerOrigin = "agent";

    /// <summary>Max size of the projected tool-result JSON handed back to the model, in bytes. A tool workflow
    /// returning megabytes must not blow up the context; oversize results are truncated with a marker.</summary>
    private const int MaxResultBytes = 16 * 1024;

    // Node types that make a workflow ineligible as a tool. aiAgent → no recursion (defense in depth on top
    // of the parent-origin guard). The trigger-entry types → a tool must start at a plain `start` node, not a
    // webhook/poll/schedule/error entry, so its contract is a callable function, not an event handler.
    private static readonly string[] ForbiddenToolNodeTypes = { "aiAgent" };
    private static readonly string[] TriggerEntryNodeTypes = { "webhookTrigger", "pollingTrigger", "scheduler", "errorTrigger" };
    private const string StartNodeType = "start";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public AgentToolRunner(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<AgentToolResult> RunToolAsync(AgentToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var workflowId = new WorkflowDefinitionId(invocation.WorkflowId);

        // --- Scope A: validate + create the seeded child instance (own AppDbContext, committed before we run). ---
        ExecutionInstanceId childId;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var versions = scope.ServiceProvider.GetRequiredService<ActiveWorkflowVersionService>();
            var definitions = scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionProvider>();

            // Recursion guard: a run already spawned as a tool (origin = agent) may not spawn further tool
            // runs. Depth cap = 1 in v1. The node also self-guards, but enforce here at the boundary too.
            var parent = await db.ExecutionInstances.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == new ExecutionInstanceId(invocation.ParentExecutionId), cancellationToken);
            if (parent is not null && string.Equals(parent.TriggerOrigin, ToolTriggerOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return Failure(Guid.Empty, "nested agent tool calls are not allowed (an agent tool may not itself run an agent).");
            }

            var version = await versions.GetActiveVersionAsync(workflowId, cancellationToken);
            if (version is null)
            {
                return Failure(Guid.Empty, $"the tool workflow '{invocation.WorkflowId}' has no published/active version.");
            }

            var definition = await definitions.GetDefinitionAsync(workflowId, cancellationToken);
            if (definition is null || definition.IsArchived)
            {
                return Failure(Guid.Empty, $"the tool workflow '{invocation.WorkflowId}' does not exist.");
            }
            if (!definition.IsEnabled)
            {
                return Failure(Guid.Empty, $"the tool workflow '{invocation.WorkflowId}' is disabled.");
            }

            if (version.Nodes.Any(n => ForbiddenToolNodeTypes.Any(t => t.Equals(n.Type, StringComparison.OrdinalIgnoreCase))))
            {
                return Failure(Guid.Empty, $"the tool workflow '{invocation.WorkflowId}' contains an AI Agent node and cannot be used as a tool (no recursion).");
            }
            if (!version.Nodes.Any(n => StartNodeType.Equals(n.Type, StringComparison.OrdinalIgnoreCase))
                || version.Nodes.Any(n => TriggerEntryNodeTypes.Any(t => t.Equals(n.Type, StringComparison.OrdinalIgnoreCase))))
            {
                return Failure(Guid.Empty, $"the tool workflow '{invocation.WorkflowId}' must begin with a plain Start node (webhook/schedule/poll/error-triggered workflows can't be tools).");
            }

            var globals = BuildSeededGlobals(invocation.Arguments);

            var child = new ExecutionInstance
            {
                Id = ExecutionInstanceId.New(),
                WorkflowDefinitionId = workflowId,
                WorkflowVersionId = version.Id,
                Status = ExecutionStatus.Pending,
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow(),
                TriggerOrigin = ToolTriggerOrigin,
                GlobalVariables = globals,
            };
            childId = child.Id;

            // Back-link so the run timeline can thread this child under the parent agent node/turn.
            var backLink = new ExecutionJournal
            {
                Id = Guid.NewGuid(),
                ExecutionInstanceId = child.Id,
                NodeId = null,
                Timestamp = _timeProvider.GetUtcNow(),
                EventType = "AgentToolLink",
                Message = $"Agent tool '{invocation.ToolName}' invoked from run {invocation.ParentExecutionId} (iteration {invocation.Iteration}).",
                Data = new Dictionary<string, object>
                {
                    ["parentExecutionId"] = invocation.ParentExecutionId.ToString(),
                    ["parentNodeId"] = invocation.ParentNodeId,
                    ["iteration"] = invocation.Iteration,
                    ["toolName"] = invocation.ToolName,
                },
            };

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.ExecutionInstances.AddAsync(child, cancellationToken);
            await db.JournalEntries.AddAsync(backLink, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // --- Scope B: drive the child synchronously to a terminal state (fresh AppDbContext + executor). ---
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<WorkflowExecutor>();
            await executor.ExecuteAsync(childId, null, null, cancellationToken);

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var finished = await db.ExecutionInstances.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == childId, cancellationToken);

            if (finished is null)
            {
                return Failure(childId.Value, "the tool run disappeared before its result could be read.");
            }
            if (finished.Status != ExecutionStatus.Completed)
            {
                // A failed child run is journaled as a normal failed run (dead-letter + error workflow fire
                // as usual, DECIDE-4); the agent just hears "it failed" and may retry differently or give up.
                return Failure(childId.Value, $"the tool run ended with status {finished.Status} instead of completing.");
            }

            var resultJson = ProjectOutputs(finished.GlobalVariables, invocation.Outputs);
            return new AgentToolResult(true, resultJson, childId.Value);
        }
    }

    /// <summary>Each validated argument becomes a named global (so the tool workflow can reference it as
    /// <c>{{name}}</c>), plus the whole argument object under <see cref="TriggerPayloadKeys.Agent"/>.</summary>
    private static Dictionary<string, object> BuildSeededGlobals(IReadOnlyDictionary<string, object?> arguments)
    {
        var globals = new Dictionary<string, object>();
        foreach (var (key, value) in arguments)
        {
            if (value is not null)
            {
                globals[key] = value;
            }
        }
        globals[TriggerPayloadKeys.Agent] = arguments.ToDictionary(kv => kv.Key, kv => kv.Value);
        return globals;
    }

    /// <summary>Projects the declared <paramref name="outputs"/> out of the finished run's globals into a JSON
    /// object. Missing keys are emitted as null and collected under a <c>"__missing"</c> note so the model can
    /// react. Oversize results are truncated with an explicit marker.</summary>
    private static string ProjectOutputs(IReadOnlyDictionary<string, object> globals, IReadOnlyList<string> outputs)
    {
        var projection = new Dictionary<string, object?>();
        var missing = new List<string>();
        foreach (var name in outputs)
        {
            if (globals.TryGetValue(name, out var value))
            {
                projection[name] = value;
            }
            else
            {
                projection[name] = null;
                missing.Add(name);
            }
        }
        if (missing.Count > 0)
        {
            projection["__missing"] = missing;
        }

        var json = JsonSerializer.Serialize(projection);
        // Cap on real UTF-8 bytes, not UTF-16 code units — otherwise the byte budget is wrong for
        // non-ASCII content (a char can be 1–4 bytes). Keep the payload valid JSON by wrapping a preview.
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxResultBytes)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            // Truncate on a byte boundary (may drop a trailing partial char → a replacement char, which
            // JSON-encodes fine); this is only a human-readable preview marker, not machine-parsed data.
            var preview = System.Text.Encoding.UTF8.GetString(bytes, 0, MaxResultBytes);
            json = JsonSerializer.Serialize(new
            {
                __truncated = true,
                __note = "the tool result exceeded the size cap and was truncated",
                preview,
            });
        }
        return json;
    }

    private static AgentToolResult Failure(Guid childId, string reason) =>
        new(false, JsonSerializer.Serialize(new { error = reason }), childId, reason);
}
