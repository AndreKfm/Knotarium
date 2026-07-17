// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.Ai;
using Knotarium.Core.Domain;
using Knotarium.Features.Ai;
using Knotarium.Features.Ai.Providers;
using Knotarium.Features.Compiler;
using Xunit;

namespace Knotarium.Tests.Ai;

public class LlmWorkflowGeneratorTests
{
    private sealed class FakeConfigStore : IAiProviderConfigStore
    {
        private readonly AiProviderConfig? _config;
        public FakeConfigStore(AiProviderConfig? config) => _config = config;
        public Task<AiProviderConfig?> GetAsync(CancellationToken ct = default) => Task.FromResult(_config);
        public Task SetAsync(AiProviderConfig config, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeProvider : ILlmChatProvider
    {
        private readonly string _text;
        public string Vendor { get; }
        public FakeProvider(string vendor, string text) { Vendor = vendor; _text = text; }
        public Task<string> CompleteAsync(LlmChatRequest request, CancellationToken ct) => Task.FromResult(_text);
    }

    private sealed class FakeSecretResolver : ISecretResolver
    {
        private readonly string? _value;
        public FakeSecretResolver(string? value) => _value = value;
        public Task<string?> ResolveAsync(string secretRef, CancellationToken ct = default) => Task.FromResult(_value);
    }

    private static IReadOnlyList<NodePackageManifest> Catalog() =>
        new List<NodePackageManifest>(new InMemoryNodePackageManifestProvider().GetAllManifests());

    private const string ValidWorkflowJson =
        """{ "name": "X", "nodes": [ { "id": "t", "type": "manualTrigger", "properties": {} } ], "edges": [] }""";

    private static LlmWorkflowGenerator Build(
        AiProviderConfig? config, string providerText = ValidWorkflowJson, string vendor = LlmVendors.Anthropic, string? apiKey = "sk-key")
        => new(
            new FakeConfigStore(config),
            new ILlmChatProvider[] { new FakeProvider(vendor, providerText) },
            new FakeSecretResolver(apiKey),
            new AiGenerationOptions());

    private static AiProviderConfig CompleteConfig(string vendor = LlmVendors.Anthropic) =>
        new(vendor, "some-model", "cred-1");

    [Fact]
    public async Task Generate_WithCompleteConfig_ParsesWorkflow()
    {
        var generator = Build(CompleteConfig());
        var attempt = await generator.GenerateAsync(new WorkflowGenerationRequest("do a thing", Catalog()));

        Assert.True(attempt.Parsed);
        Assert.NotNull(attempt.Workflow);
        Assert.Single(attempt.Workflow!.Nodes);
    }

    [Fact]
    public async Task Generate_NotConfigured_Throws()
    {
        var generator = Build(config: null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateAsync(new WorkflowGenerationRequest("x", Catalog())));
        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public async Task Generate_IncompleteConfig_Throws()
    {
        // Missing credential ref → IsComplete is false.
        var generator = Build(new AiProviderConfig(LlmVendors.OpenAi, "gpt-4o", ""));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateAsync(new WorkflowGenerationRequest("x", Catalog())));
    }

    [Fact]
    public async Task Generate_UnresolvedKey_Throws()
    {
        var generator = Build(CompleteConfig(), apiKey: null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateAsync(new WorkflowGenerationRequest("x", Catalog())));
        Assert.Contains("could not be resolved", ex.Message);
    }

    [Fact]
    public async Task Generate_UnknownVendor_NoAdapter_Throws()
    {
        // Config names a vendor for which no adapter is registered (fake provider is 'anthropic').
        var generator = Build(new AiProviderConfig(LlmVendors.Gemini, "m", "cred-1"), vendor: LlmVendors.Anthropic);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateAsync(new WorkflowGenerationRequest("x", Catalog())));
        Assert.Contains("adapter", ex.Message);
    }

    [Fact]
    public async Task Generate_MalformedProviderOutput_YieldsParseError()
    {
        var generator = Build(CompleteConfig(), providerText: "not json");
        var attempt = await generator.GenerateAsync(new WorkflowGenerationRequest("x", Catalog()));

        Assert.False(attempt.Parsed);
        Assert.NotNull(attempt.ParseError);
    }

    [Fact]
    public async Task Generate_Refine_PreservesCurrentWorkflowId()
    {
        // Refining an existing workflow must keep its id so a save updates it in place, not creates a new one.
        var original = new WorkflowDefinition(
            WorkflowDefinitionId.Create("orig-id-123"), "Original",
            new[] { new NodeDefinition(NodeId.Create("t"), "manualTrigger", new Dictionary<string, object>()) },
            Array.Empty<EdgeDefinition>());
        var generator = Build(CompleteConfig());

        var attempt = await generator.GenerateAsync(
            new WorkflowGenerationRequest("add a log node", Catalog(), PriorErrors: null, CurrentWorkflow: original));

        Assert.True(attempt.Parsed);
        Assert.Equal("orig-id-123", attempt.Workflow!.Id.Value);
    }
}
