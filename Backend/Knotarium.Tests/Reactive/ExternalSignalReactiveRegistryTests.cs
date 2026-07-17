// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Api.Services;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Knotarium.Tests.Reactive;

public class ExternalSignalReactiveRegistryTests
{
    private static NodeDefinition Device(string id, string targetId) =>
        new(NodeId.Create(id), "externalDevice", new Dictionary<string, object>
        {
            ["targetId"] = new Dictionary<string, object> { ["value"] = targetId },
        });

    private static WorkflowDefinition DeviceGraph(bool enabled = true) =>
        new(
            new WorkflowDefinitionId("wf-1"),
            "device graph",
            new List<NodeDefinition> { Device("A", "siteA"), Device("B", "siteB") },
            new List<EdgeDefinition>
            {
                new("e1", NodeId.Create("A"), "evt:VehicleRecognised", NodeId.Create("B"), "act:StartRecording"),
            })
        { IsEnabled = enabled };

    private static ExternalSignalTriggerRegistry NewRegistry(IExternalSignalProvider provider, IExternalSignalRunEnqueuer? enqueuer = null, bool armed = true)
    {
        var services = new ServiceCollection();
        if (enqueuer != null)
        {
            services.AddSingleton(enqueuer);
        }
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new ExternalSignalTriggerRegistry(scopeFactory, NullLogger<ExternalSignalTriggerRegistry>.Instance, new RuntimeArmingState(armed), provider);
    }

    // A device graph whose event pin is wired to a normal (non-device) node — the imperative bridge.
    private static WorkflowDefinition EventToLogGraph(string eventPin) =>
        new(
            new WorkflowDefinitionId("wf-log"),
            "event to log",
            new List<NodeDefinition>
            {
                Device("A", "siteA"),
                new(NodeId.Create("L"), "log", new Dictionary<string, object> { ["message"] = "hi" }),
            },
            new List<EdgeDefinition>
            {
                new("e1", NodeId.Create("A"), $"evt:{eventPin}", NodeId.Create("L"), "in"),
            })
        { IsEnabled = true };

    [Fact]
    public async Task Device_event_wired_to_a_normal_node_starts_a_run_seeded_at_that_node()
    {
        var provider = new FakeProvider();
        var enqueuer = new FakeEnqueuer();
        var registry = NewRegistry(provider, enqueuer);
        await registry.SyncAsync(EventToLogGraph("1:started"));

        // Subscribed by the bare event type.
        var sub = Assert.Single(provider.Subscriptions);
        Assert.Equal("1", sub.Filter.Type);

        await provider.FireAsync("siteA", "1", active: true);

        var run = Assert.Single(enqueuer.Runs);
        Assert.Equal("wf-log", run.WorkflowId.Value);
        Assert.Equal(new[] { "L" }, run.EntryNodeIds);
        Assert.Empty(provider.Sent); // a normal-node bridge dispatches no device action
    }

    // A device graph reacting to an INCOMING action (action pin as a source → normal node).
    private static WorkflowDefinition ActionToLogGraph(string actionType) =>
        new(
            new WorkflowDefinitionId("wf-act"),
            "action to log",
            new List<NodeDefinition>
            {
                Device("A", "siteA"),
                new(NodeId.Create("L"), "log", new Dictionary<string, object> { ["message"] = "hi" }),
            },
            new List<EdgeDefinition>
            {
                new("e1", NodeId.Create("A"), $"act:{actionType}", NodeId.Create("L"), "in"),
            })
        { IsEnabled = true };

    [Fact]
    public async Task Incoming_action_wired_to_a_normal_node_subscribes_by_action_kind_and_starts_a_run()
    {
        var provider = new FakeProvider();
        var enqueuer = new FakeEnqueuer();
        var registry = NewRegistry(provider, enqueuer);
        await registry.SyncAsync(ActionToLogGraph("CameraCycle"));

        var sub = Assert.Single(provider.Subscriptions);
        Assert.Equal(ExternalSignalKind.Action, sub.Filter.Kind);
        Assert.Equal("CameraCycle", sub.Filter.Type);

        // Incoming actions carry no lifecycle phase (Active = null) and still fire.
        await provider.FireAsync("siteA", "CameraCycle", active: null, kind: ExternalSignalKind.Action);

        var run = Assert.Single(enqueuer.Runs);
        Assert.Equal("wf-act", run.WorkflowId.Value);
        Assert.Equal(new[] { "L" }, run.EntryNodeIds);
        Assert.Empty(provider.Sent); // reacting to an action never dispatches a command back
    }

    [Fact]
    public async Task Repeated_delivery_of_the_same_signal_instance_starts_only_one_run()
    {
        var provider = new FakeProvider();
        var enqueuer = new FakeEnqueuer();
        var registry = NewRegistry(provider, enqueuer);
        await registry.SyncAsync(EventToLogGraph("1:started"));

        // Same event instance (same correlation key + phase) delivered twice — e.g. recovery re-sends a
        // still-active event as "started" on reconnect. Only the first should start a run.
        await provider.FireAsync("siteA", "1", active: true, correlationKey: "evt-instance-7");
        await provider.FireAsync("siteA", "1", active: true, correlationKey: "evt-instance-7");

        Assert.Single(enqueuer.Runs);
    }

    [Fact]
    public async Task Distinct_signal_instances_each_start_a_run()
    {
        var provider = new FakeProvider();
        var enqueuer = new FakeEnqueuer();
        var registry = NewRegistry(provider, enqueuer);
        await registry.SyncAsync(EventToLogGraph("1:started"));

        // A genuine fresh start has a new instance id, so it is not suppressed.
        await provider.FireAsync("siteA", "1", active: true, correlationKey: "evt-instance-1");
        await provider.FireAsync("siteA", "1", active: true, correlationKey: "evt-instance-2");

        Assert.Equal(2, enqueuer.Runs.Count);
    }

    [Fact]
    public async Task Signals_without_a_correlation_key_are_not_de_duped()
    {
        var provider = new FakeProvider();
        var enqueuer = new FakeEnqueuer();
        var registry = NewRegistry(provider, enqueuer);
        await registry.SyncAsync(EventToLogGraph("1:started"));

        // No instance id → can't be recognized as a duplicate; both fire (no regression for such sources).
        await provider.FireAsync("siteA", "1", active: true, correlationKey: null);
        await provider.FireAsync("siteA", "1", active: true, correlationKey: null);

        Assert.Equal(2, enqueuer.Runs.Count);
    }

    [Fact]
    public async Task Disarmed_runtime_drops_the_inbound_signal_and_starts_no_run()
    {
        var provider = new FakeProvider();
        var enqueuer = new FakeEnqueuer();
        var registry = NewRegistry(provider, enqueuer, armed: false);
        await registry.SyncAsync(EventToLogGraph("1:started"));

        // Subscription is still registered (connection stays live), but a fire starts nothing.
        await provider.FireAsync("siteA", "1", active: true);

        Assert.Empty(enqueuer.Runs);
        Assert.Empty(provider.Sent);
    }

    [Fact]
    public async Task Device_event_run_respects_the_pin_phase()
    {
        var provider = new FakeProvider();
        var enqueuer = new FakeEnqueuer();
        var registry = NewRegistry(provider, enqueuer);
        await registry.SyncAsync(EventToLogGraph("1:stopped"));

        await provider.FireAsync("siteA", "1", active: true);
        Assert.Empty(enqueuer.Runs);

        await provider.FireAsync("siteA", "1", active: false);
        Assert.Single(enqueuer.Runs);
    }

    [Fact]
    public async Task Enabling_a_device_graph_subscribes_the_trigger_and_acquires_both_targets()
    {
        var provider = new FakeProvider();
        var registry = NewRegistry(provider);

        await registry.SyncAsync(DeviceGraph());

        var sub = Assert.Single(provider.Subscriptions);
        Assert.Equal(ExternalSignalKind.Event, sub.Filter.Kind);
        Assert.Equal("siteA", sub.Filter.TargetId);
        Assert.Equal("VehicleRecognised", sub.Filter.Type);
        Assert.Contains("siteA", provider.Acquired);
        Assert.Contains("siteB", provider.Acquired);
    }

    [Fact]
    public async Task Inbound_event_dispatches_the_effect_action_to_the_other_target()
    {
        var provider = new FakeProvider();
        var registry = NewRegistry(provider);
        await registry.SyncAsync(DeviceGraph());

        await provider.FireAsync("siteA", "VehicleRecognised");

        var dispatched = Assert.Single(provider.Sent);
        Assert.Equal(ExternalSignalKind.Action, dispatched.Kind);
        Assert.Equal("siteB", dispatched.TargetId);
        Assert.Equal("StartRecording", dispatched.Type);
        Assert.False(string.IsNullOrWhiteSpace(dispatched.CorrelationKey));
    }

    [Fact]
    public async Task Disabling_tears_down_subscriptions_so_no_dispatch_happens()
    {
        var provider = new FakeProvider();
        var registry = NewRegistry(provider);
        await registry.SyncAsync(DeviceGraph());

        await registry.SyncAsync(DeviceGraph(enabled: false));

        Assert.True(provider.Subscriptions[0].Disposed);
        await provider.FireAsync("siteA", "VehicleRecognised");
        Assert.Empty(provider.Sent);
    }

    // A device graph whose event pin carries a phase suffix ("3:started" / "3:stopped").
    private static WorkflowDefinition PhaseGraph(string eventPin) =>
        new(
            new WorkflowDefinitionId("wf-phase"),
            "phase device graph",
            new List<NodeDefinition> { Device("A", "siteA"), Device("B", "siteB") },
            new List<EdgeDefinition>
            {
                new("e1", NodeId.Create("A"), $"evt:{eventPin}", NodeId.Create("B"), "act:Record"),
            })
        { IsEnabled = true };

    [Fact]
    public async Task Phase_pin_subscribes_by_the_bare_event_type_not_the_suffixed_value()
    {
        var provider = new FakeProvider();
        var registry = NewRegistry(provider);

        await registry.SyncAsync(PhaseGraph("3:started"));

        var sub = Assert.Single(provider.Subscriptions);
        Assert.Equal("3", sub.Filter.Type);
    }

    [Fact]
    public async Task Started_pin_fires_on_active_and_ignores_the_stop_transition()
    {
        var provider = new FakeProvider();
        var registry = NewRegistry(provider);
        await registry.SyncAsync(PhaseGraph("3:started"));

        await provider.FireAsync("siteA", "3", active: false);
        Assert.Empty(provider.Sent);

        await provider.FireAsync("siteA", "3", active: true);
        Assert.Single(provider.Sent);
    }

    [Fact]
    public async Task Stopped_pin_fires_only_on_the_stop_transition()
    {
        var provider = new FakeProvider();
        var registry = NewRegistry(provider);
        await registry.SyncAsync(PhaseGraph("3:stopped"));

        await provider.FireAsync("siteA", "3", active: true);
        Assert.Empty(provider.Sent);

        await provider.FireAsync("siteA", "3", active: false);
        Assert.Single(provider.Sent);
    }

    [Fact]
    public async Task Bare_pin_defaults_to_started_so_a_stop_does_not_fire()
    {
        var provider = new FakeProvider();
        var registry = NewRegistry(provider);
        await registry.SyncAsync(PhaseGraph("3"));

        await provider.FireAsync("siteA", "3", active: false);
        Assert.Empty(provider.Sent);

        await provider.FireAsync("siteA", "3", active: true);
        Assert.Single(provider.Sent);
    }

    private static WorkflowDefinition GatedGraph() =>
        new(
            new WorkflowDefinitionId("wf-2"),
            "gated device graph",
            new List<NodeDefinition>
            {
                Device("A", "siteA"),
                new(NodeId.Create("C"), "condition", new Dictionary<string, object>
                {
                    ["logic"] = JsonSerializer.Deserialize<JsonElement>(
                        """{ "version":2, "root": { "kind":"cmp","id":"c","op":"eq", "a": { "kind":"ref","type":"string","ref": { "__type":"variable_ref","variableName":"plate" } }, "b": { "kind":"lit","type":"string","value":"ABC" } } }"""),
                }),
                Device("B", "siteB"),
            },
            new List<EdgeDefinition>
            {
                new("e1", NodeId.Create("A"), "evt:Plate", NodeId.Create("C"), "in"),
                new("e2", NodeId.Create("C"), "true", NodeId.Create("B"), "act:Record"),
            });

    [Fact]
    public async Task Condition_guard_blocks_dispatch_when_the_payload_fails()
    {
        var provider = new FakeProvider();
        var registry = NewRegistry(provider);
        await registry.SyncAsync(GatedGraph());

        await provider.FireAsync("siteA", "Plate", JsonSerializer.Deserialize<JsonElement>("""{ "plate": "ZZZ" }"""));

        Assert.Empty(provider.Sent);
    }

    [Fact]
    public async Task Condition_guard_allows_dispatch_when_the_payload_passes()
    {
        var provider = new FakeProvider();
        var registry = NewRegistry(provider);
        await registry.SyncAsync(GatedGraph());

        await provider.FireAsync("siteA", "Plate", JsonSerializer.Deserialize<JsonElement>("""{ "plate": "ABC" }"""));

        var dispatched = Assert.Single(provider.Sent);
        Assert.Equal("siteB", dispatched.TargetId);
        Assert.Equal("Record", dispatched.Type);
    }

    // ── Fake enqueuer ────────────────────────────────────────────────────────
    private sealed class FakeEnqueuer : IExternalSignalRunEnqueuer
    {
        public sealed record Run(WorkflowDefinitionId WorkflowId, string[] EntryNodeIds, DeviceEventProvenance? Provenance = null);

        public List<Run> Runs { get; } = new();

        public Task<bool> EnqueueAsync(WorkflowDefinitionId workflowId, InboundEnvelope envelope, CancellationToken cancellationToken)
        {
            Runs.Add(new Run(workflowId, Array.Empty<string>()));
            return Task.FromResult(true);
        }

        public Task<bool> EnqueueFromDeviceEventAsync(
            WorkflowDefinitionId workflowId,
            InboundEnvelope envelope,
            IReadOnlyCollection<string> entryNodeIds,
            CancellationToken cancellationToken,
            DeviceEventProvenance? provenance = null)
        {
            Runs.Add(new Run(workflowId, entryNodeIds.ToArray(), provenance));
            return Task.FromResult(true);
        }

        public Task<ExecutionInstanceId?> StartDeviceEventRunAsync(
            WorkflowDefinitionId workflowId,
            InboundEnvelope envelope,
            IReadOnlyCollection<string> entryNodeIds,
            CancellationToken cancellationToken,
            DeviceEventProvenance? provenance = null)
        {
            Runs.Add(new Run(workflowId, entryNodeIds.ToArray(), provenance));
            return Task.FromResult<ExecutionInstanceId?>(ExecutionInstanceId.New());
        }
    }

    // ── Fake provider ────────────────────────────────────────────────────────
    private sealed class FakeProvider : IExternalSignalProvider
    {
        public sealed record Sub(SignalSubscription Filter, Func<InboundEnvelope, Task> Handler)
        {
            public bool Disposed { get; set; }
        }

        public List<Sub> Subscriptions { get; } = new();
        public ConcurrentBag<string> Acquired { get; } = new();
        public ConcurrentBag<OutboundSignal> Sent { get; } = new();

        public IDisposable Subscribe(SignalSubscription filter, Func<InboundEnvelope, Task> handler)
        {
            var sub = new Sub(filter, handler);
            Subscriptions.Add(sub);
            return new Disposer(() => sub.Disposed = true);
        }

        public IAsyncDisposable Acquire(string targetId)
        {
            Acquired.Add(targetId);
            return new AsyncDisposer();
        }

        public Task<DispatchResult> SendAsync(OutboundSignal signal, CancellationToken cancellationToken)
        {
            Sent.Add(signal);
            return Task.FromResult(DispatchResult.Ok(signal.CorrelationKey));
        }

        public async Task FireAsync(string targetId, string type, JsonElement payload = default, bool? active = true,
            ExternalSignalKind kind = ExternalSignalKind.Event, string? correlationKey = null)
        {
            var env = new InboundEnvelope(
                SystemId: "sys", TargetId: targetId, Host: "h",
                Kind: kind, Type: type,
                GlobalCameraNumber: null, ChannelId: null, Active: active,
                CorrelationKey: correlationKey, Payload: payload, Timestamp: DateTimeOffset.UnixEpoch);
            foreach (var sub in Subscriptions)
            {
                if (sub.Disposed) continue;
                if (sub.Filter.Kind == kind && sub.Filter.TargetId == targetId && sub.Filter.Type == type)
                {
                    await sub.Handler(env);
                }
            }
        }

        public TargetStatus GetStatus(string targetId) => new(targetId, TargetConnectivity.Online);
        public Task<IReadOnlyList<RunningSignal>> GetRunningAsync(string targetId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RunningSignal>>(Array.Empty<RunningSignal>());
        public Task<TargetCatalog> SyncCatalogAsync(string targetId, CancellationToken cancellationToken)
            => Task.FromResult(new TargetCatalog(targetId, Array.Empty<CatalogChannel>(), Array.Empty<CatalogEntry>(), Array.Empty<CatalogEntry>()));
        public event EventHandler<string>? CatalogChanged { add { } remove { } }

        private sealed class Disposer : IDisposable
        {
            private readonly Action _onDispose;
            public Disposer(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }

        private sealed class AsyncDisposer : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
