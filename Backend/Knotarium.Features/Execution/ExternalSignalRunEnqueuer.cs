using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Features.Execution;

/// <summary>
/// Default enqueuer for external-signal-triggered runs: creates an ExecutionInstance carrying the
/// normalized inbound envelope and queues it. Mirrors <see cref="Knotarium.Features.Polling.PollRunEnqueuer"/>
/// but with its own trigger origin + payload key so external-signal runs are distinguishable.
/// </summary>
public sealed class ExternalSignalRunEnqueuer : IExternalSignalRunEnqueuer
{
    /// <summary>Global-variable key under which the inbound envelope is exposed to the workflow.</summary>
    public const string PayloadVariableKey = TriggerPayloadKeys.ExternalSignal;

    /// <summary>Clean, expression-friendly alias for the inbound signal (subset of the envelope).</summary>
    public const string SignalVariableKey = "signal";

    /// <summary>
    /// Project the inbound envelope into a tidy, lower-cased view for expressions/logic:
    /// <c>signal.kind/type/active/target/camera/channel/correlationKey</c> plus
    /// <c>signal.params</c> (the raw payload — the event/action parameters).
    /// </summary>
    private static Dictionary<string, object?> SignalView(InboundEnvelope e)
    {
        var view = new Dictionary<string, object?>
        {
            ["kind"] = e.Kind == ExternalSignalKind.Action ? "action" : "event",
            ["type"] = e.Type,
            ["active"] = e.Active,
            ["target"] = e.TargetId,
            ["camera"] = e.GlobalCameraNumber,
            ["channel"] = e.ChannelId,
            ["correlationKey"] = e.CorrelationKey,
            ["params"] = e.Payload,
        };

        // Friendly, action-named alias for the payload, NESTED under `signal` so it's unmistakably this
        // run's inbound signal (per-execution-instance — parallel event runs never share it), not a free
        // global: `signal.customAction.String` reads the same payload as `signal.params.String`. Guarded
        // so an (unlikely) action whose camelCased name hits a reserved key above doesn't clobber it.
        var alias = TypeAlias(e.Type);
        if (alias is not null && !view.ContainsKey(alias))
        {
            view[alias] = e.Payload;
        }
        return view;
    }

    /// <summary>
    /// Camel-cased, identifier-safe alias for a signal type, used as a friendly payload global name
    /// (e.g. "CustomAction" → "customAction" so logic reads `customAction.String`). Returns null when the
    /// type can't be a variable head (empty, doesn't start with a letter/underscore, or has punctuation —
    /// e.g. a numeric event type id), in which case callers fall back to `signal.params`.
    /// </summary>
    public static string? TypeAlias(string? type)
    {
        if (string.IsNullOrEmpty(type)) return null;
        if (!char.IsLetter(type[0]) && type[0] != '_') return null;
        foreach (var c in type)
        {
            if (!char.IsLetterOrDigit(c) && c != '_') return null;
        }
        return char.ToLowerInvariant(type[0]) + type.Substring(1);
    }

    /// <summary>Trigger origin marking a run started from a device-block event pin wired to normal nodes.</summary>
    public const string DeviceEventTriggerOrigin = "deviceEvent";

    /// <summary>Global-variable key holding the explicit entry node ids for a device-event run.</summary>
    public const string EntryNodesVariableKey = "__deviceEventEntryNodes";

    /// <summary>Global-variable key holding the id of the device block whose pin fired this run.</summary>
    public const string SourceNodeVariableKey = "__deviceEventSourceNode";

    /// <summary>Global-variable key holding a human-readable label for the fired pin (e.g. "Event 3 ▸ Started").</summary>
    public const string FiredPinVariableKey = "__deviceEventFiredPin";

    private readonly AppDbContext _dbContext;
    private readonly WorkflowExecutionQueue _queue;
    private readonly ActiveWorkflowVersionService _activeWorkflowVersionService;
    private readonly TimeProvider _timeProvider;

    public ExternalSignalRunEnqueuer(
        AppDbContext dbContext,
        WorkflowExecutionQueue queue,
        ActiveWorkflowVersionService activeWorkflowVersionService,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _activeWorkflowVersionService = activeWorkflowVersionService ?? throw new ArgumentNullException(nameof(activeWorkflowVersionService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<bool> EnqueueAsync(WorkflowDefinitionId workflowId, InboundEnvelope envelope, CancellationToken cancellationToken)
        => await EnqueueCoreAsync(workflowId, envelope, "externalSignal", entryNodeIds: null, provenance: null, cancellationToken) is not null;

    public async Task<bool> EnqueueFromDeviceEventAsync(
        WorkflowDefinitionId workflowId,
        InboundEnvelope envelope,
        IReadOnlyCollection<string> entryNodeIds,
        CancellationToken cancellationToken,
        DeviceEventProvenance? provenance = null)
        => await EnqueueCoreAsync(workflowId, envelope, DeviceEventTriggerOrigin, entryNodeIds, provenance, cancellationToken) is not null;

    public Task<ExecutionInstanceId?> StartDeviceEventRunAsync(
        WorkflowDefinitionId workflowId,
        InboundEnvelope envelope,
        IReadOnlyCollection<string> entryNodeIds,
        CancellationToken cancellationToken,
        DeviceEventProvenance? provenance = null)
        => EnqueueCoreAsync(workflowId, envelope, DeviceEventTriggerOrigin, entryNodeIds, provenance, cancellationToken);

    private async Task<ExecutionInstanceId?> EnqueueCoreAsync(
        WorkflowDefinitionId workflowId,
        InboundEnvelope envelope,
        string triggerOrigin,
        IReadOnlyCollection<string>? entryNodeIds,
        DeviceEventProvenance? provenance,
        CancellationToken cancellationToken)
    {
        var version = await _activeWorkflowVersionService.GetActiveVersionAsync(workflowId, cancellationToken);
        if (version is null)
        {
            return null;
        }

        var globals = new Dictionary<string, object>
        {
            [PayloadVariableKey] = envelope,
            // Clean, expression-friendly view of the inbound signal for logic (Condition/Log/Set
            // Variable): `signal.type`, `signal.active`, `signal.camera`, `signal.params.<field>`, …
            // The raw envelope stays under __externalSignal for anything that needs the full shape.
            [SignalVariableKey] = SignalView(envelope),
        };
        if (entryNodeIds is { Count: > 0 })
        {
            // A device-event run begins at the pin's downstream nodes, not a compiled trigger; carry the
            // entry ids so the executor seeds the run there (see ResolveEntryNodesForTriggerOriginAsync).
            globals[EntryNodesVariableKey] = new List<string>(entryNodeIds);
        }
        if (provenance is not null)
        {
            // Origin of a device-event run: the device block whose pin fired, and a label for that pin.
            // The executor surfaces these on completion so the source node reads "Triggered · <pin>" in the
            // timeline instead of a phantom "Pending" (a device block never runs as an ordinary work node).
            globals[SourceNodeVariableKey] = provenance.SourceNodeId;
            globals[FiredPinVariableKey] = provenance.FiredPinLabel;
        }

        var execution = new ExecutionInstance
        {
            Id = ExecutionInstanceId.New(),
            WorkflowDefinitionId = workflowId,
            WorkflowVersionId = version.Id,
            Status = ExecutionStatus.Pending,
            CreatedAt = _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow(),
            TriggerOrigin = triggerOrigin,
            GlobalVariables = globals
        };

        await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            await _dbContext.ExecutionInstances.AddAsync(execution, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        _queue.QueueExecution(execution.Id);
        return execution.Id;
    }
}
