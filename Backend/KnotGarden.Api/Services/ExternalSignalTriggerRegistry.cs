using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Core.Reactive;
using KnotGarden.Features.Reactive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KnotGarden.Api.Services;

/// <summary>
/// Trigger-activation registry for inbound external signals — the in-process analogue of the
/// schedule/polling synchronizers. For each ENABLED workflow it registers the inbound subscriptions
/// declared by its Event/Action Trigger nodes with the <see cref="IExternalSignalProvider"/>, and it
/// holds a lifecycle acquisition per subscription so the provider connects lazily and stays connected
/// while at least one trigger is active. Connection refcounts are therefore held by registered
/// subscriptions, not by executions — letting an inbound signal start a workflow when nothing is
/// running. Disabling/deleting a workflow disposes its subscriptions (and releases its refcounts).
///
/// Generic: the host recognizes the generic node types <c>eventTrigger</c>/<c>actionTrigger</c> and
/// reads generic, vendor-neutral properties. No specific provider is named.
/// </summary>
public sealed class ExternalSignalTriggerRegistry
{
    private readonly IExternalSignalProvider? _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExternalSignalTriggerRegistry> _logger;
    private readonly RuntimeArmingState _armingState;

    private readonly object _gate = new();
    // workflowId -> the disposables (subscriptions + lifecycle acquisitions) registered for it.
    private readonly Dictionary<string, List<IDisposable>> _subscriptions = new();
    private readonly Dictionary<string, List<IAsyncDisposable>> _acquisitions = new();
    // Suppresses repeated deliveries of the same inbound signal instance (recovery re-sends, double
    // pushes) per handler, so a fresh transition fires but a replay of an already-handled one does not.
    private readonly InboundSignalDedupe _dedupe = new();

    public ExternalSignalTriggerRegistry(
        IServiceScopeFactory scopeFactory,
        ILogger<ExternalSignalTriggerRegistry> logger,
        RuntimeArmingState armingState,
        IExternalSignalProvider? provider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _armingState = armingState;
        _provider = provider;
    }

    /// <summary>True when an external-signal provider is loaded (a host plugin contributed one).</summary>
    public bool HasProvider => _provider != null;

    /// <summary>
    /// Reconcile the live subscriptions for one workflow against its current definition + enabled state.
    /// Always tears down the workflow's existing subscriptions first, then re-registers if eligible.
    /// </summary>
    public async Task SyncAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var key = workflow.Id.Value;

        await RemoveAsync(workflow.Id, cancellationToken);

        if (_provider == null || !workflow.IsEnabled || workflow.IsArchived)
        {
            return;
        }

        var triggerNodes = workflow.Nodes
            .Where(n => n.Type.Equals("eventTrigger", StringComparison.OrdinalIgnoreCase)
                     || n.Type.Equals("actionTrigger", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Reactive device-block wiring compiles to standing rules dispatched directly on the provider
        // bus (no workflow run). A workflow may have only reactive rules, only trigger nodes, or both.
        var reactiveRules = ReactiveRuleCompiler.Compile(workflow);

        // Device inbound-signal pins (events AND incoming actions) wired to ordinary nodes bridge the
        // device bus into the imperative run engine: an inbound signal starts a workflow run seeded at
        // the pin's downstream node.
        var signalTriggers = ReactiveRuleCompiler.CompileSignalTriggers(workflow);

        if (triggerNodes.Count == 0 && reactiveRules.Count == 0 && signalTriggers.Count == 0)
        {
            return;
        }

        var subs = new List<IDisposable>();
        var acqs = new List<IAsyncDisposable>();

        foreach (var node in triggerNodes)
        {
            try
            {
                var kind = node.Type.Equals("eventTrigger", StringComparison.OrdinalIgnoreCase)
                    ? ExternalSignalKind.Event
                    : ExternalSignalKind.Action;

                var targetId = ReadString(node, "instance", "targetId");
                var type = ReadString(node, "event", "action", "signalType", "type");
                var channelId = ReadString(node, "channelId", "channel");
                var globalCameraNumber = ReadLong(node, "globalCameraNumber");

                var filter = new SignalSubscription(
                    Kind: kind,
                    TargetId: string.IsNullOrWhiteSpace(targetId) ? null : targetId,
                    Type: string.IsNullOrWhiteSpace(type) ? null : type,
                    GlobalCameraNumber: globalCameraNumber,
                    ChannelId: string.IsNullOrWhiteSpace(channelId) ? null : channelId);

                // Optional field-level filter: only start a run when a chosen JSON key of the inbound
                // signal's payload compares true (e.g. action field Direction == "Left"). The key is
                // authored from the provider's static field schema; the compare is evaluated below.
                var predicate = ReadFieldPredicate(node);

                var workflowId = workflow.Id;
                var nodeId = node.Id.Value;

                var subscription = _provider.Subscribe(filter, envelope => HandleInboundAsync(workflowId, nodeId, predicate, envelope));
                subs.Add(subscription);

                if (!string.IsNullOrWhiteSpace(targetId))
                {
                    acqs.Add(_provider.Acquire(targetId));
                }

                _logger.LogInformation(
                    "Registered external-signal trigger: workflow '{Workflow}' node '{Node}' ({Kind} target='{Target}' type='{Type}').",
                    key, nodeId, kind, targetId ?? "*", type ?? "*");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register external-signal trigger for workflow '{Workflow}' node '{Node}'.", key, node.Id.Value);
            }
        }

        // RunMode.Reactive: register each compiled rule's trigger as an inbound Event subscription whose
        // handler dispatches the rule's effects straight back through the provider — no run engine, so
        // it scales to high-frequency events and stays live while the workflow is enabled. Refcounts on
        // both the trigger target and every effect target are held by these registrations.
        foreach (var rule in reactiveRules)
        {
            try
            {
                // An event pin may carry a phase suffix ("3:started"/"3:stopped"). Subscribe by the bare
                // event type so we still match the inbound envelope (whose Type has no suffix); the phase
                // is enforced against the envelope's Active flag at dispatch.
                var (baseEventType, _) = ReactiveEventPhase.Parse(rule.Trigger.SignalType);

                var filter = new SignalSubscription(
                    Kind: ExternalSignalKind.Event,
                    TargetId: rule.Trigger.TargetId,
                    Type: baseEventType);

                var capturedRule = rule;
                var subscription = _provider.Subscribe(filter, envelope => DispatchReactiveAsync(key, capturedRule, envelope));
                subs.Add(subscription);

                acqs.Add(_provider.Acquire(rule.Trigger.TargetId));
                foreach (var effectTarget in rule.Effects.Select(e => e.TargetId).Distinct(StringComparer.Ordinal))
                {
                    acqs.Add(_provider.Acquire(effectTarget));
                }

                _logger.LogInformation(
                    "Registered reactive rule: workflow '{Workflow}' trigger ({Target}/{Event}) -> {EffectCount} effect(s).",
                    key, rule.Trigger.TargetId, rule.Trigger.SignalType, rule.Effects.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register reactive rule '{Rule}' for workflow '{Workflow}'.", rule.Id, key);
            }
        }

        // Imperative bridge: each device signal pin (event or incoming action) wired to a normal node
        // subscribes by its bare type (event phase enforced at dispatch) and, on a matching inbound
        // signal, starts a workflow run seeded at the pin's downstream node.
        foreach (var trigger in signalTriggers)
        {
            try
            {
                var (baseType, _) = ReactiveEventPhase.Parse(trigger.SignalType);

                var filter = new SignalSubscription(
                    Kind: trigger.Kind,
                    TargetId: trigger.TargetId,
                    Type: baseType);

                var capturedTrigger = trigger;
                var workflowId = workflow.Id;
                var subscription = _provider.Subscribe(filter, envelope => DispatchDeviceSignalRunAsync(workflowId, capturedTrigger, envelope));
                subs.Add(subscription);

                acqs.Add(_provider.Acquire(trigger.TargetId));

                _logger.LogInformation(
                    "Registered device-signal trigger: workflow '{Workflow}' ({Kind} {Target}/{Type}) -> run from node '{Node}'.",
                    key, trigger.Kind, trigger.TargetId, trigger.SignalType, trigger.EntryNodeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register device-signal trigger ({Kind} {Target}/{Type}) for workflow '{Workflow}'.", trigger.Kind, trigger.TargetId, trigger.SignalType, key);
            }
        }

        lock (_gate)
        {
            _subscriptions[key] = subs;
            _acquisitions[key] = acqs;
        }
    }

    /// <summary>
    /// Inbound handler for a device-signal trigger: enforce the pin's phase against the envelope (events
    /// carry a started/stopped phase; incoming actions have none and always pass), then enqueue an
    /// imperative run seeded at the pin's downstream node — the ordinary engine, not a provider dispatch.
    /// </summary>
    private async Task DispatchDeviceSignalRunAsync(WorkflowDefinitionId workflowId, ReactiveSignalTrigger trigger, InboundEnvelope envelope)
    {
        // Global kill-switch: while disarmed, automatic execution is paused — an inbound signal starts
        // no run (only a manual Run does). Drop it here rather than tearing down the live subscription.
        if (!_armingState.IsArmed)
        {
            return;
        }

        var (_, phase) = ReactiveEventPhase.Parse(trigger.SignalType);
        if (!ReactiveEventPhase.Matches(phase, envelope.Active))
        {
            return;
        }

        // De-dupe: the same signal instance (same correlation key + phase) can be delivered more than once
        // — e.g. state recovery re-sends a still-active event as "started" on (re)connect. React to a fresh
        // transition, not to repeats of one we already handled for this pin.
        if (!_dedupe.TryAccept($"sig|{workflowId.Value}|{trigger.EntryNodeId}", envelope))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var enqueuer = scope.ServiceProvider.GetRequiredService<IExternalSignalRunEnqueuer>();
            var provenance = new DeviceEventProvenance(
                trigger.SourceNodeId,
                FormatFiredPinLabel(trigger.Kind, trigger.SignalType));
            var started = await enqueuer.EnqueueFromDeviceEventAsync(
                workflowId, envelope, new[] { trigger.EntryNodeId }, CancellationToken.None, provenance);
            if (!started)
            {
                _logger.LogWarning(
                    "Device-event run for workflow '{Workflow}' ({Target}/{Event}) dropped: no active runtime version.",
                    workflowId.Value, trigger.TargetId, trigger.SignalType);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to enqueue device-event run for workflow '{Workflow}' ({Target}/{Event}).",
                workflowId.Value, trigger.TargetId, trigger.SignalType);
        }
    }

    /// <summary>
    /// Human-readable label for the fired pin, matching the editor's device-pin wording ("Event 3 ▸ Started").
    /// Events carry a phase (bare defaults to Started); incoming actions have none, so the action type stands
    /// alone. Best-effort: a purely-numeric event id is prefixed with "Event"; a named one is used verbatim.
    /// </summary>
    internal static string FormatFiredPinLabel(ExternalSignalKind kind, string signalType)
    {
        if (kind == ExternalSignalKind.Action)
        {
            return string.IsNullOrWhiteSpace(signalType) ? "Action" : $"Action · {signalType}";
        }

        var (baseType, phase) = ReactiveEventPhase.Parse(signalType);
        var phaseLabel = phase == EventPhase.Stopped ? "Stopped" : "Started";
        if (string.IsNullOrWhiteSpace(baseType))
        {
            return $"Event ▸ {phaseLabel}";
        }
        var isNumeric = baseType.All(char.IsDigit);
        var head = isNumeric ? $"Event {baseType}" : baseType;
        return $"{head} ▸ {phaseLabel}";
    }

    /// <summary>
    /// Inbound handler for a reactive rule: dispatch every effect directly through the provider. Runs
    /// on the provider's bus, never the run engine. Each dispatch carries a correlation key derived
    /// from the rule + inbound signal so the provider's idempotency window suppresses duplicate fires.
    /// </summary>
    private async Task DispatchReactiveAsync(string workflowKey, ReactiveRule rule, InboundEnvelope envelope)
    {
        if (_provider == null)
        {
            return;
        }

        // Global kill-switch: while disarmed, no automatic effect is dispatched.
        if (!_armingState.IsArmed)
        {
            return;
        }

        // Phase gate: a pin targeting a specific lifecycle edge ("3:started"/"3:stopped") only fires when
        // the inbound event's Active flag matches. A bare pin defaults to Started, so it reacts to the
        // event's onset (and lifecycle-less events) but not its stop transition.
        var (_, phase) = ReactiveEventPhase.Parse(rule.Trigger.SignalType);
        if (!ReactiveEventPhase.Matches(phase, envelope.Active))
        {
            return;
        }

        // De-dupe repeated deliveries of the same signal instance (e.g. recovery re-sends on reconnect).
        if (!_dedupe.TryAccept($"rule|{workflowKey}|{rule.Id}", envelope))
        {
            return;
        }

        // Logic on the wire: process the path steps in order — Set Variable(s) transforms mutate the
        // dispatch context, Condition guards gate — and dispatch only if every guard clears. A direct
        // wire has no steps and always proceeds.
        if (!ReactiveStepProcessor.Passes(rule.Steps, envelope))
        {
            return;
        }

        var correlation = envelope.CorrelationKey
            ?? envelope.Timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        foreach (var effect in rule.Effects)
        {
            try
            {
                var signal = new OutboundSignal(
                    Kind: ExternalSignalKind.Action,
                    Type: effect.SignalType,
                    TargetId: effect.TargetId,
                    CorrelationKey: $"{rule.Id}->{effect.TargetId}:{effect.SignalType}#{correlation}");

                var result = await _provider.SendAsync(signal, CancellationToken.None);
                if (!result.Accepted)
                {
                    _logger.LogWarning(
                        "Reactive effect not accepted: workflow '{Workflow}' rule '{Rule}' -> {Target}/{Action}: {Error}.",
                        workflowKey, rule.Id, effect.TargetId, effect.SignalType, result.Error ?? "unknown");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Reactive effect dispatch failed: workflow '{Workflow}' rule '{Rule}' -> {Target}/{Action}.",
                    workflowKey, rule.Id, effect.TargetId, effect.SignalType);
            }
        }
    }

    /// <summary>Tear down all live subscriptions for one workflow (on disable/delete/resync).</summary>
    public async Task RemoveAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default)
    {
        List<IDisposable>? subs;
        List<IAsyncDisposable>? acqs;
        lock (_gate)
        {
            _subscriptions.Remove(workflowId.Value, out subs);
            _acquisitions.Remove(workflowId.Value, out acqs);
        }

        if (subs != null)
        {
            foreach (var s in subs)
            {
                try { s.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Error disposing subscription."); }
            }
        }
        if (acqs != null)
        {
            foreach (var a in acqs)
            {
                try { await a.DisposeAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Error releasing acquisition."); }
            }
        }
    }

    private async Task HandleInboundAsync(WorkflowDefinitionId workflowId, string nodeId, InboundFieldPredicate? predicate, InboundEnvelope envelope)
    {
        // Global kill-switch: while disarmed, an inbound Event/Action trigger starts no run.
        if (!_armingState.IsArmed)
        {
            return;
        }

        // Field-level filter: when configured, only proceed if the chosen payload key compares true.
        // Evaluated before de-dupe so a non-matching signal neither runs nor consumes a dedupe slot.
        if (predicate is not null && !InboundFieldPredicate.Matches(predicate, envelope))
        {
            return;
        }

        // De-dupe repeated deliveries of the same signal instance (e.g. recovery re-sends on reconnect).
        if (!_dedupe.TryAccept($"node|{workflowId.Value}|{nodeId}", envelope))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var enqueuer = scope.ServiceProvider.GetRequiredService<IExternalSignalRunEnqueuer>();
            var started = await enqueuer.EnqueueAsync(workflowId, envelope, CancellationToken.None);
            if (!started)
            {
                _logger.LogWarning(
                    "Inbound signal for workflow '{Workflow}' node '{Node}' dropped: no active runtime version.",
                    workflowId.Value, nodeId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue run for inbound signal on workflow '{Workflow}' node '{Node}'.", workflowId.Value, nodeId);
        }
    }

    private static string? ReadString(NodeDefinition node, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (node.Properties.TryGetValue(key, out var raw) && raw is not null)
            {
                var s = raw.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }
        }
        return null;
    }

    private static long? ReadLong(NodeDefinition node, string key)
    {
        if (node.Properties.TryGetValue(key, out var raw) && raw is not null
            && long.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }
        return null;
    }

    /// <summary>Read the optional inbound field filter (matchField/matchOperator/matchValue). Null when
    /// no field is chosen — the trigger then fires on any signal of its type, as before.</summary>
    private static InboundFieldPredicate? ReadFieldPredicate(NodeDefinition node)
    {
        var field = ReadString(node, "matchField");
        if (string.IsNullOrWhiteSpace(field))
        {
            return null;
        }
        var op = ReadString(node, "matchOperator");
        var value = ReadString(node, "matchValue");
        return new InboundFieldPredicate(field!, string.IsNullOrWhiteSpace(op) ? "equals" : op!, value);
    }

    /// <summary>
    /// Per-handler suppression of repeated deliveries of the same inbound signal INSTANCE. The identity is
    /// the (scope, type, active, correlationKey) tuple — scope being the receiving rule/trigger/pin, so a
    /// single signal fanned out to several handlers still fires each, while a replay of one already handled
    /// (state recovery re-sending an active event as "started" on reconnect, a double push) is dropped.
    /// A signal with no correlation key can't be identified as an instance, so it is never de-duped.
    /// Bounded LRU; in-memory, so it resets on restart (a recovery right after a restart fires once).
    /// </summary>
    private sealed class InboundSignalDedupe
    {
        private const int Capacity = 1024;
        private readonly object _gate = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();

        public bool TryAccept(string scope, InboundEnvelope envelope)
        {
            if (string.IsNullOrEmpty(envelope.CorrelationKey))
            {
                return true; // no instance identity → can't be a recognizable duplicate
            }

            var key = $"{scope}|{envelope.Type}|{envelope.Active}|{envelope.CorrelationKey}";
            lock (_gate)
            {
                if (!_seen.Add(key))
                {
                    return false; // already handled this instance for this handler
                }
                _order.Enqueue(key);
                if (_order.Count > Capacity)
                {
                    _seen.Remove(_order.Dequeue());
                }
                return true;
            }
        }
    }
}

/// <summary>
/// A single field-level filter on an Event/Action Trigger: react only when payload key <see cref="Field"/>
/// compares true under <see cref="Operator"/> against <see cref="Value"/>. The field is resolved with the
/// same precedence as the reactive wire (payload field first, then a few envelope conveniences such as
/// <c>type</c>/<c>camera</c>/<c>channel</c>/<c>active</c>), so a graph can filter on signal content without
/// a downstream run. Compares are type-aware: numeric when both sides parse as numbers, else case-insensitive
/// string. Fails closed — an absent field makes every compare (except <c>notExists</c>) false.
/// </summary>
public sealed record InboundFieldPredicate(string Field, string Operator, string? Value)
{
    public static bool Matches(InboundFieldPredicate predicate, InboundEnvelope envelope)
    {
        var found = new ReactiveContext(envelope).TryResolve(predicate.Field, out var raw);
        var actual = found ? Stringify(raw) : null;
        var op = predicate.Operator?.Trim().ToLowerInvariant();

        // Presence operators look only at whether the key resolved to a non-empty value.
        if (op is "exists") return !string.IsNullOrEmpty(actual);
        if (op is "notexists") return string.IsNullOrEmpty(actual);

        if (actual is null) return false; // compare against a missing field → no match (fail closed)
        var expected = predicate.Value ?? string.Empty;

        var bothNumeric = TryNum(actual, out var a) & TryNum(expected, out var b);
        return op switch
        {
            "equals" or null or "" => bothNumeric ? a == b : string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "notequals" => bothNumeric ? a != b : !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "contains" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "notcontains" => !actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "greaterthan" => bothNumeric ? a > b : string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase) > 0,
            "lessthan" => bothNumeric ? a < b : string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase) < 0,
            _ => false, // unknown operator → fail closed
        };
    }

    private static bool TryNum(string s, out double value)
        => double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);

    private static string? Stringify(object? value) => value switch
    {
        null => null,
        string s => s,
        System.Text.Json.JsonElement e => e.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => e.GetString(),
            System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => null,
            _ => e.ToString(),
        },
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}

/// <summary>
/// On host startup, registers Event/Action Trigger subscriptions for every already-enabled workflow,
/// so inbound signals can start workflows immediately after a restart (the in-memory registry has no
/// DB backing to rehydrate from). No-op when no provider is loaded.
/// </summary>
public sealed class ExternalSignalStartupReconciler : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly ExternalSignalTriggerRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExternalSignalStartupReconciler> _logger;

    public ExternalSignalStartupReconciler(
        ExternalSignalTriggerRegistry registry,
        IServiceScopeFactory scopeFactory,
        ILogger<ExternalSignalStartupReconciler> logger)
    {
        _registry = registry;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_registry.HasProvider)
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IWorkflowStore>();
            var workflows = await store.ListAsync(cancellationToken);
            var registered = 0;
            foreach (var workflow in workflows.Where(w => w.IsEnabled && !w.IsArchived))
            {
                await _registry.SyncAsync(workflow, cancellationToken);
                registered++;
            }
            _logger.LogInformation("External-signal startup reconciliation registered triggers for {Count} workflow(s).", registered);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "External-signal startup reconciliation failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
