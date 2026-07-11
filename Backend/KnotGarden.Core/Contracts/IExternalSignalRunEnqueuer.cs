using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Domain;

namespace KnotGarden.Core.Contracts;

/// <summary>
/// Where a device-event run came from: the device block whose pin fired (<see cref="SourceNodeId"/>) and a
/// human-readable label for that pin (<see cref="FiredPinLabel"/>, e.g. "Event 3 ▸ Started"). Carried on the
/// run so the timeline can show the origin node as "Triggered · &lt;pin&gt;" instead of a phantom "Pending"
/// (a device block never executes as a work node — one pin fires and seeds a downstream branch).
/// </summary>
public sealed record DeviceEventProvenance(string SourceNodeId, string FiredPinLabel);

/// <summary>
/// Starts a workflow run in response to an inbound external signal (an Event/Action Trigger firing).
/// Mirrors the polling enqueuer: creates a Pending execution carrying the normalized envelope as a
/// global variable, then queues it. Returns false when the workflow has no active runtime version.
/// </summary>
public interface IExternalSignalRunEnqueuer
{
    Task<bool> EnqueueAsync(WorkflowDefinitionId workflowId, InboundEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>
    /// Start a run for a device-block event pin wired to ordinary nodes: like
    /// <see cref="EnqueueAsync(WorkflowDefinitionId, InboundEnvelope, CancellationToken)"/> but the run
    /// begins flowing from <paramref name="entryNodeIds"/> (the pin's downstream nodes) instead of from a
    /// compiled trigger node, so the device event drives the imperative graph from exactly that wire.
    /// </summary>
    Task<bool> EnqueueFromDeviceEventAsync(
        WorkflowDefinitionId workflowId,
        InboundEnvelope envelope,
        IReadOnlyCollection<string> entryNodeIds,
        CancellationToken cancellationToken,
        DeviceEventProvenance? provenance = null);

    /// <summary>
    /// As <see cref="EnqueueFromDeviceEventAsync"/>, but returns the created execution id (or
    /// <see langword="null"/> when the workflow has no active version). Used by the editor's
    /// "simulate signal" action so the caller can navigate to the run it started.
    /// </summary>
    Task<ExecutionInstanceId?> StartDeviceEventRunAsync(
        WorkflowDefinitionId workflowId,
        InboundEnvelope envelope,
        IReadOnlyCollection<string> entryNodeIds,
        CancellationToken cancellationToken,
        DeviceEventProvenance? provenance = null);
}
