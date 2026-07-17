// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Knotarium.Core.Contracts;

namespace Knotarium.Core.Reactive;

/// <summary>
/// One end of a reactive wire: a signal on a specific external target. For a trigger it is the event
/// that fires the rule; for an effect it is the action to dispatch. Vendor-neutral — the provider
/// resolves <see cref="TargetId"/> and <see cref="SignalType"/> against whatever system it fronts.
/// </summary>
public sealed record ReactiveSignalRef(string TargetId, string SignalType);

/// <summary>
/// A device-block inbound signal pin (an event OR an incoming action raised by the device) wired to a
/// normal (non-device) workflow node — the bridge from the device bus into the imperative run engine. It
/// STARTS a workflow run seeded at <see cref="EntryNodeId"/> (the pin's downstream node) so the inbound
/// signal can drive ordinary nodes (Log, notifications, HTTP…). <see cref="Kind"/> selects the inbound
/// subscription (event vs action); <see cref="SignalType"/> carries the (possibly phase-qualified for
/// events) pin value. <see cref="SourceNodeId"/> is the device block the pin belongs to — carried so a
/// device-event run can name its origin ("Triggered · Event 3 ▸ Started" on that node) in the timeline.
/// </summary>
public sealed record ReactiveSignalTrigger(ExternalSignalKind Kind, string TargetId, string SignalType, string EntryNodeId, string SourceNodeId);

/// <summary>
/// One node encountered along a wire between an event-output pin and the action-input pins it feeds.
/// Steps are processed in path order at dispatch: a <see cref="ReactiveTransform"/> mutates the
/// evaluation context; a <see cref="ReactiveGuard"/> gates the rest of the path. <see cref="SourceNodeId"/>
/// identifies the originating node (part of the stable grouping key).
/// </summary>
public abstract record ReactiveStep(string SourceNodeId);

/// <summary>
/// A Condition node on the wire, captured as a gate the inbound signal must clear before the rule's
/// effects fire. <see cref="Logic"/> is the condition node's persisted <c>logic</c> payload (opaque
/// here — parsed + evaluated against the dispatch context at run time); <see cref="ExpectTrue"/>
/// records which branch the path followed (the <c>true</c> output requires the condition to evaluate
/// true; the <c>false</c> output requires false). Guards on a path are ANDed.
/// </summary>
public sealed record ReactiveGuard(string SourceNodeId, bool ExpectTrue, object Logic) : ReactiveStep(SourceNodeId);

/// <summary>One name → value assignment captured from a Set Variable(s) node. <see cref="Value"/> is
/// opaque to this layer: a literal, or a reference spec resolved against the dispatch context.</summary>
public sealed record ReactiveAssignment(string Name, object? Value);

/// <summary>
/// A Set Variable(s) node on the wire: its assignments are applied to the dispatch context (in order,
/// before any later guard reads them). Values are literals or references into the inbound signal /
/// earlier variables; expression (<c>{{ }}</c>) values are not supported on a reactive wire.
/// </summary>
public sealed record ReactiveTransform(string SourceNodeId, IReadOnlyList<ReactiveAssignment> Assignments)
    : ReactiveStep(SourceNodeId);

/// <summary>
/// A standing reactive rule compiled from a device-block graph: when <see cref="Trigger"/> (an event
/// on one target) fires, process the path <see cref="Steps"/> in order (transforms mutate the context,
/// guards gate); if every guard clears, dispatch each <see cref="Effects"/> entry (an action on a
/// possibly different target). Cross-instance falls out naturally — the trigger names block A's target,
/// the effects name block B's. A direct device→device wire has no steps.
/// </summary>
public sealed record ReactiveRule(
    string Id,
    ReactiveSignalRef Trigger,
    IReadOnlyList<ReactiveSignalRef> Effects,
    IReadOnlyList<ReactiveStep> Steps)
{
    /// <summary>The guard steps on this rule's path, in order (convenience over <see cref="Steps"/>).</summary>
    public IReadOnlyList<ReactiveGuard> Guards => Steps.OfType<ReactiveGuard>().ToList();
}
