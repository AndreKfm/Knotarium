// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Api.Services.Ai;
using Knotarium.Features.Ai;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Xunit;

namespace Knotarium.Tests.Ai;

public class GeneratedCredentialFinalizerTests
{
    private sealed class FakeManifestProvider : INodePackageManifestProvider
    {
        private readonly Dictionary<string, NodePackageManifest> _byType;
        public FakeManifestProvider(params NodePackageManifest[] manifests)
            => _byType = manifests.ToDictionary(m => m.Id.Value, System.StringComparer.Ordinal);

        public Task<NodePackageManifest?> GetManifestAsync(NodePackageId packageId, CancellationToken cancellationToken = default)
        {
            _byType.TryGetValue(packageId.Value, out var m);
            return Task.FromResult<NodePackageManifest?>(m);
        }
    }

    private static NodePackageManifest CredNode(string type, params string[] credParamNames)
    {
        var parameters = credParamNames.Select(n => new ParameterDefinition(n, "credentialRef", true, false)).ToList();
        return new NodePackageManifest(
            new NodePackageId(type), "1.0.0", type, "Integrations",
            NodeTier.Declarative, NodeSideEffectKind.NonIdempotentSideEffect, RecoveryMode.FailImmediately,
            30, new List<string>(), parameters, new List<OutputDefinition> { new("result") });
    }

    private static NodePackageManifest PlainNode(string type) =>
        new(new NodePackageId(type), "1.0.0", type, "Utility",
            NodeTier.Declarative, NodeSideEffectKind.IdempotentSideEffect, RecoveryMode.FailImmediately,
            10, new List<string>(),
            new List<ParameterDefinition> { new("message", "string", false, true) },
            new List<OutputDefinition> { new("result") });

    private static WorkflowDefinition WorkflowWith(params NodeDefinition[] nodes) =>
        new(WorkflowDefinitionId.New(), "wf", nodes, System.Array.Empty<EdgeDefinition>());

    [Fact]
    public async Task Finalize_NonSlotCredentialValue_IsRewrittenToSlotAndReported()
    {
        var node = new NodeDefinition(NodeId.Create("n1"), "apiNode",
            new Dictionary<string, object> { ["apiKey"] = "Weather API" });
        var finalizer = new GeneratedCredentialFinalizer(new FakeManifestProvider(CredNode("apiNode", "apiKey")));

        var result = await finalizer.FinalizeAsync(WorkflowWith(node));

        Assert.Equal("slot:weather-api", result.Workflow.Nodes[0].Properties["apiKey"]);
        Assert.Contains("weather-api", result.OpenSlots);
    }

    [Fact]
    public async Task Finalize_AlreadySlotValue_IsPreserved()
    {
        var node = new NodeDefinition(NodeId.Create("n1"), "apiNode",
            new Dictionary<string, object> { ["apiKey"] = "slot:my-key" });
        var finalizer = new GeneratedCredentialFinalizer(new FakeManifestProvider(CredNode("apiNode", "apiKey")));

        var result = await finalizer.FinalizeAsync(WorkflowWith(node));

        Assert.Equal("slot:my-key", result.Workflow.Nodes[0].Properties["apiKey"]);
        Assert.Equal(new[] { "my-key" }, result.OpenSlots);
    }

    [Fact]
    public async Task Finalize_DistinctFabricatedValues_GetDistinctSlotKeys()
    {
        var a = new NodeDefinition(NodeId.Create("a"), "apiNode",
            new Dictionary<string, object> { ["apiKey"] = "Service" });
        var b = new NodeDefinition(NodeId.Create("b"), "apiNode",
            new Dictionary<string, object> { ["apiKey"] = "Service" });
        var finalizer = new GeneratedCredentialFinalizer(new FakeManifestProvider(CredNode("apiNode", "apiKey")));

        var result = await finalizer.FinalizeAsync(WorkflowWith(a, b));

        var keyA = (string)result.Workflow.Nodes[0].Properties["apiKey"];
        var keyB = (string)result.Workflow.Nodes[1].Properties["apiKey"];
        Assert.NotEqual(keyA, keyB); // collision suffixing kicks in (-2)
        Assert.Equal(2, result.OpenSlots.Count);
    }

    [Fact]
    public async Task Finalize_NodeWithoutCredentialParams_IsUntouched()
    {
        var node = new NodeDefinition(NodeId.Create("n1"), "log",
            new Dictionary<string, object> { ["message"] = "hi" });
        var finalizer = new GeneratedCredentialFinalizer(new FakeManifestProvider(PlainNode("log")));

        var result = await finalizer.FinalizeAsync(WorkflowWith(node));

        Assert.Equal("hi", result.Workflow.Nodes[0].Properties["message"]);
        Assert.Empty(result.OpenSlots);
    }
}
