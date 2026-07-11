using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Reactive;
using KnotGarden.Features.Condition;

namespace KnotGarden.Features.Reactive;

/// <summary>
/// The mutable evaluation context for a reactive rule dispatch: the inbound signal plus any variables
/// set by Set Variable(s) transform steps along the wire. A reference resolves to a variable set on the
/// wire first, then to a field of the inbound payload, then to a few envelope-level conveniences.
/// </summary>
public sealed class ReactiveContext
{
    private readonly Dictionary<string, object?> _vars = new(StringComparer.Ordinal);

    public ReactiveContext(InboundEnvelope envelope) => Envelope = envelope;

    public InboundEnvelope Envelope { get; }

    public void Set(string name, object? value) => _vars[name] = value;

    /// <summary>Resolve a reference by name: wire variables win, then payload fields, then envelope conveniences.</summary>
    public bool TryResolve(string name, out object? value)
    {
        if (_vars.TryGetValue(name, out value))
        {
            return true;
        }
        if (Envelope.Payload.ValueKind == JsonValueKind.Object && Envelope.Payload.TryGetProperty(name, out var prop))
        {
            value = prop;
            return true;
        }
        switch (name)
        {
            case "type": value = Envelope.Type; return true;
            case "targetId": value = Envelope.TargetId; return true;
            case "active":
                if (Envelope.Active.HasValue) { value = Envelope.Active.Value; return true; }
                break;
            case "camera":
            case "globalCameraNumber":
                if (Envelope.GlobalCameraNumber.HasValue) { value = Envelope.GlobalCameraNumber.Value; return true; }
                break;
            case "channel":
            case "channelId":
                if (Envelope.ChannelId is not null) { value = Envelope.ChannelId; return true; }
                break;
            case "correlationKey":
                if (Envelope.CorrelationKey is not null) { value = Envelope.CorrelationKey; return true; }
                break;
        }
        value = null;
        return false;
    }
}

/// <summary>
/// Evaluates a reactive wire's Condition guard against the dispatch context — the bridge that lets the
/// type-aware Condition engine gate a standing reactive rule (no workflow run). The guard's persisted
/// <c>logic</c> is parsed once and its operand references are resolved against the
/// <see cref="ReactiveContext"/>. Leaf semantics + aggregation are the unchanged
/// <see cref="ConditionEvaluator"/> shared with the run path.
///
/// Fails closed: an unparseable guard, an unresolved reference, or an Error/Incomplete makes the guard
/// NOT clear — a reactive effect never fires on ambiguous logic.
/// </summary>
public static class ReactiveConditionEvaluator
{
    /// <summary>True when the guard clears for this dispatch context (its effects may fire).</summary>
    public static bool Passes(ReactiveGuard guard, ReactiveContext context)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(context);

        if (!ConditionLogicParser.TryParse(guard.Logic, out var logic, out _) || logic is null)
        {
            return false; // unparseable / unconfigured → fail closed
        }

        var resolved = ResolveNode(logic.Root, context);
        var outcome = ConditionEvaluator.EvaluateTree(resolved);
        return outcome.Status switch
        {
            ConditionStatus.True => guard.ExpectTrue,
            ConditionStatus.False => !guard.ExpectTrue,
            _ => false, // Error / Incomplete → fail closed
        };
    }

    /// <summary>Convenience: evaluate a single guard against a context built from the envelope (no wire vars).</summary>
    public static bool Passes(ReactiveGuard guard, InboundEnvelope envelope)
        => Passes(guard, new ReactiveContext(envelope));

    /// <summary>Guards are ANDed; effects fire only if every guard clears (evaluated with no wire vars).</summary>
    public static bool AllPass(IReadOnlyList<ReactiveGuard> guards, InboundEnvelope envelope)
    {
        if (guards is null || guards.Count == 0) return true;
        var ctx = new ReactiveContext(envelope);
        return guards.All(g => Passes(g, ctx));
    }

    private static ResolvedLogicNode ResolveNode(LogicNode node, ReactiveContext context) => node switch
    {
        ComparatorNode c => new ResolvedComparatorNode(new ResolvedComparator(
            c.Id, c.Op, ResolveOperand(c.A, context), c.B is null ? null : ResolveOperand(c.B, context))),
        GroupNode g => new ResolvedGroupNode(g.Op, g.Children.Select(child => ResolveNode(child, context)).ToList()),
        NotNode n => new ResolvedNotNode(ResolveNode(n.Child, context)),
        _ => throw new InvalidOperationException("Unknown logic node kind."),
    };

    private static ResolvedOperand ResolveOperand(PersistedOperand operand, ReactiveContext context)
    {
        if (operand.Kind == OperandKind.Lit)
        {
            return ResolvedOperand.Value(operand.Type, operand.Value);
        }

        var name = ReactiveRefs.ReadVariableName(operand.Ref);
        if (name is null)
        {
            return ResolvedOperand.Unresolved(operand.Type); // expression refs unsupported on the wire
        }
        return context.TryResolve(name, out var value)
            ? ResolvedOperand.Value(operand.Type, value)
            : ResolvedOperand.Unresolved(operand.Type);
    }
}
