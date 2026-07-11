using System;

namespace KnotGarden.Core.Reactive;

/// <summary>
/// Which lifecycle edge of a stateful external event a reactive pin reacts to. A device-block event
/// pin may target a specific phase by suffixing its signal type — <c>"3:started"</c> / <c>"3:stopped"</c>
/// — so the editor can offer "Event 002 ▸ Started" and "Event 002 ▸ Stopped" as distinct pins. An
/// unsuffixed type defaults to <see cref="Started"/>, the common "fired" edge, so an existing bare wire
/// reacts to the event's onset (and ignores its stop transition).
/// </summary>
public enum EventPhase
{
    /// <summary>The event becoming active (its onset). Also matches lifecycle-less events (no Active flag).</summary>
    Started,

    /// <summary>The event ending (its stop transition).</summary>
    Stopped,
}

/// <summary>
/// The phase-suffix convention shared by the event-pin option loader (which emits phase-qualified pin
/// values) and the trigger registry (which strips the suffix to subscribe by the bare event type and
/// gates dispatch on the inbound <see cref="Contracts.InboundEnvelope.Active"/> flag). Vendor-neutral:
/// it knows only the generic <c>&lt;type&gt;:&lt;phase&gt;</c> convention, never a provider.
/// </summary>
public static class ReactiveEventPhase
{
    public const string StartedSuffix = ":started";
    public const string StoppedSuffix = ":stopped";

    /// <summary>Build a phase-qualified pin value ("3" + Stopped → "3:stopped").</summary>
    public static string Qualify(string baseType, EventPhase phase)
        => baseType + (phase == EventPhase.Stopped ? StoppedSuffix : StartedSuffix);

    /// <summary>
    /// Split a (possibly phase-qualified) signal type into its bare event type and the targeted phase.
    /// Only a trailing <c>:started</c>/<c>:stopped</c> is read as a phase, so event ids that themselves
    /// contain a colon are left intact. An unsuffixed type defaults to <see cref="EventPhase.Started"/>.
    /// </summary>
    public static (string BaseType, EventPhase Phase) Parse(string signalType)
    {
        if (signalType is null)
        {
            return (string.Empty, EventPhase.Started);
        }
        if (signalType.EndsWith(StoppedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return (signalType[..^StoppedSuffix.Length], EventPhase.Stopped);
        }
        if (signalType.EndsWith(StartedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return (signalType[..^StartedSuffix.Length], EventPhase.Started);
        }
        return (signalType, EventPhase.Started);
    }

    /// <summary>
    /// Whether an inbound signal's <paramref name="active"/> flag satisfies the pinned phase. A
    /// <see cref="EventPhase.Stopped"/> pin fires only on an explicit stop (Active=false); a
    /// <see cref="EventPhase.Started"/> pin fires on onset (Active=true) and on lifecycle-less events
    /// (Active=null), so non-stateful events still flow through a bare wire.
    /// </summary>
    public static bool Matches(EventPhase phase, bool? active)
        => phase == EventPhase.Stopped ? active == false : active != false;
}
