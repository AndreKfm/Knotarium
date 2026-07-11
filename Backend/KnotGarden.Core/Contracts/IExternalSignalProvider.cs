using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KnotGarden.Core.Contracts;

/// <summary>
/// Generic host-resident seam for an external reactive signalling system (actions + stateful events
/// raised by / sent to an out-of-host platform). The host and the graph nodes bind ONLY to this
/// contract; the concrete provider is supplied by a binary <see cref="IHostPlugin"/> at startup and
/// is the single place that touches any vendor SDK. Keeping the seam vendor-neutral lets the host
/// stay agnostic — the provider is one of potentially many, never the only one.
///
/// A "target" is one configured connection endpoint (one box). The provider owns the lifecycle,
/// reconnection, state recovery and normalization behind this interface.
/// </summary>
public interface IExternalSignalProvider
{
    /// <summary>
    /// Reference-counted lifecycle handle for a target. The provider connects lazily on the first
    /// acquisition and tears the connection down when the last handle is disposed. Trigger
    /// activations (not executions) hold these handles, so inbound signals can start workflows when
    /// no execution is running.
    /// </summary>
    IAsyncDisposable Acquire(string targetId);

    /// <summary>Observed (never persisted) runtime status for a target.</summary>
    TargetStatus GetStatus(string targetId);

    /// <summary>
    /// Outbound dispatch (action or event start/stop). <see cref="OutboundSignal.CorrelationKey"/>
    /// round-trips on the wire for Wait-For-Event matching and outbound idempotency.
    /// </summary>
    Task<DispatchResult> SendAsync(OutboundSignal signal, CancellationToken cancellationToken);

    /// <summary>
    /// Register an inbound subscription. Subscriptions are registered by the trigger-activation
    /// registry, recomputed on trigger change — the provider derives the minimal wire subscription
    /// set from the registered filters (never a firehose). Dispose to unregister.
    /// </summary>
    IDisposable Subscribe(SignalSubscription filter, Func<InboundEnvelope, Task> handler);

    /// <summary>
    /// Currently-active (enumerable) events for a target — used on (re)connect to recover state and
    /// to resolve waits whose event is already active.
    /// </summary>
    Task<IReadOnlyList<RunningSignal>> GetRunningAsync(string targetId, CancellationToken cancellationToken);

    /// <summary>Live discovery: read the target's catalog (channels/events/actions) for dropdowns.</summary>
    Task<TargetCatalog> SyncCatalogAsync(string targetId, CancellationToken cancellationToken);

    /// <summary>Raised (with the targetId) when a target's catalog changes underneath us.</summary>
    event EventHandler<string> CatalogChanged;
}

/// <summary>Whether a signal is a transient action (fire-and-forget) or a stateful event.</summary>
public enum ExternalSignalKind
{
    Action = 0,
    Event = 1,
}

/// <summary>What to do with an event-kind outbound signal.</summary>
public enum ExternalEventCommand
{
    Start = 0,
    Stop = 1,
}

/// <summary>
/// Outbound dispatch request. Address EITHER by <see cref="GlobalCameraNumber"/> (system-wide; the
/// provider resolves the owning target) OR explicitly by <see cref="TargetId"/> +
/// <see cref="ChannelId"/>. <see cref="CorrelationKey"/> is the native correlation + idempotency key.
/// </summary>
public sealed record OutboundSignal(
    ExternalSignalKind Kind,
    string Type,
    long? GlobalCameraNumber = null,
    string? TargetId = null,
    string? ChannelId = null,
    ExternalEventCommand EventCommand = ExternalEventCommand.Start,
    string? CorrelationKey = null,
    IReadOnlyDictionary<string, JsonElement>? Parameters = null);

/// <summary>Result of an outbound dispatch.</summary>
public sealed record DispatchResult(
    bool Accepted,
    string? CorrelationKey = null,
    long? AssignedId = null,
    string? Error = null,
    bool Deduplicated = false)
{
    public static DispatchResult Ok(string? correlationKey = null, long? assignedId = null, bool deduplicated = false)
        => new(true, correlationKey, assignedId, null, deduplicated);

    public static DispatchResult Fail(string error) => new(false, null, null, error);
}

/// <summary>
/// Inbound subscription filter. Null members widen the match. The provider uses the union of all
/// registered filters to compute the minimal demand-driven wire subscription.
/// </summary>
public sealed record SignalSubscription(
    ExternalSignalKind Kind,
    string? TargetId = null,
    string? Type = null,
    long? GlobalCameraNumber = null,
    string? ChannelId = null);

/// <summary>
/// Normalized inbound signal — downstream nodes don't care which box or SDK it came from.
/// </summary>
public sealed record InboundEnvelope(
    string SystemId,
    string TargetId,
    string Host,
    ExternalSignalKind Kind,
    string Type,
    long? GlobalCameraNumber,
    string? ChannelId,
    bool? Active,
    string? CorrelationKey,
    JsonElement Payload,
    DateTimeOffset Timestamp);

/// <summary>Connectivity state of a target. Observed live; never persisted into config.</summary>
public enum TargetConnectivity
{
    Offline = 0,
    Connecting = 1,
    Online = 2,
    Faulted = 3,
}

public sealed record TargetStatus(
    string TargetId,
    TargetConnectivity Connectivity,
    DateTimeOffset? LastConnected = null,
    DateTimeOffset? LastSignal = null,
    string? LastError = null,
    int FailedDispatches = 0);

/// <summary>A currently-active event recovered from a target.</summary>
public sealed record RunningSignal(
    string TargetId,
    string Type,
    string? CorrelationKey,
    long? AssignedId,
    long? GlobalCameraNumber,
    DateTimeOffset? Since,
    JsonElement? Payload);

/// <summary>Discovery catalog for a target — drives the cascaded editor pickers.</summary>
public sealed record TargetCatalog(
    string TargetId,
    IReadOnlyList<CatalogChannel> Channels,
    IReadOnlyList<CatalogEntry> Events,
    IReadOnlyList<CatalogEntry> Actions);

public sealed record CatalogChannel(
    string ChannelId,
    string DisplayName,
    long GlobalCameraNumber);

public sealed record CatalogEntry(
    string Id,
    string DisplayName,
    string? Description = null);
