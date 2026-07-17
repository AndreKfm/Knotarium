// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.Ai;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Xunit;

namespace Knotarium.Tests.Nodes;

public class AiDiffNodeTaskTests
{
    private sealed class ScriptedChat : IChatCompletionService
    {
        private readonly Queue<string> _replies;
        public List<ChatCompletionRequest> Requests { get; } = new();
        public ScriptedChat(params string[] replies) => _replies = new Queue<string>(replies);
        public Task<string> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(_replies.Dequeue());
        }
    }

    private static NodeExecutionContext Context(Dictionary<string, object> inputs) => new(
        WorkflowId: WorkflowDefinitionId.New(),
        ExecutionId: Guid.NewGuid(),
        NodeId: NodeId.Create("diff-1"),
        Inputs: inputs,
        GlobalVariables: new Dictionary<string, object>());

    private static Dictionary<string, object> Inputs(string previous, string current) => new()
    {
        ["previous"] = previous,
        ["current"] = current,
    };

    // --- required-input guards ---

    [Fact]
    public async Task MissingCurrent_Fails()
    {
        var task = new AiDiffNodeTask(new ScriptedChat("unused"));
        var result = await task.ExecuteAsync(Context(new() { ["previous"] = "x" }), CancellationToken.None);
        Assert.Contains("both 'previous' and 'current' are required", Assert.IsType<LegacyNodeResult.Failure>(result).ErrorMessage);
    }

    // --- deterministic short-circuit ---

    [Fact]
    public async Task Identical_documents_route_none_without_calling_the_model()
    {
        var chat = new ScriptedChat();  // no replies queued — a call would throw
        var task = new AiDiffNodeTask(chat);

        var result = await task.ExecuteAsync(
            Context(Inputs("  The terms are unchanged. ", "The terms are unchanged.")), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("none", success.Outputs!["selectedPort"]);
        Assert.Empty(chat.Requests);   // the LLM was never called
    }

    // --- routing by deterministic verdict ---

    [Fact]
    public async Task Material_change_routes_material_and_surfaces_top_impact()
    {
        var chat = new ScriptedChat("""
            { "materialChanges": [ { "type": "deadline_changed", "old": "30 September", "new": "15 August", "impact": "high" } ],
              "ignoredChanges": [ "Whitespace" ] }
            """);
        var task = new AiDiffNodeTask(chat);

        var result = await task.ExecuteAsync(
            Context(Inputs("Deliver by 30 September.", "Deliver by 15 August. ")), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("material", success.Outputs!["selectedPort"]);
        var record = Assert.IsType<Dictionary<string, object>>(success.Outputs!["result"]);
        Assert.Equal("high", record["impact"]);
    }

    [Fact]
    public async Task Only_ignored_changes_route_cosmetic()
    {
        var chat = new ScriptedChat("""
            { "materialChanges": [], "ignoredChanges": [ "Reworded intro", "Formatting" ] }
            """);
        var task = new AiDiffNodeTask(chat);

        var result = await task.ExecuteAsync(
            Context(Inputs("Hello there.", "Hi there!")), CancellationToken.None);

        Assert.Equal("cosmetic", Assert.IsType<LegacyNodeResult.Success>(result).Outputs!["selectedPort"]);
    }

    [Fact]
    public async Task No_reported_changes_route_none()
    {
        var chat = new ScriptedChat("""{ "materialChanges": [], "ignoredChanges": [] }""");
        var task = new AiDiffNodeTask(chat);

        var result = await task.ExecuteAsync(Context(Inputs("a", "b")), CancellationToken.None);
        Assert.Equal("none", Assert.IsType<LegacyNodeResult.Success>(result).Outputs!["selectedPort"]);
    }

    // --- aggregation + impact units ---

    [Fact]
    public void AggregateChangeType_prefers_material_over_cosmetic_over_none()
    {
        var one = new AiDiffNodeTask.MaterialChange("t", "a", "b", "low");
        Assert.Equal("material", AiDiffNodeTask.AggregateChangeType(new[] { one }, Array.Empty<string>()));
        Assert.Equal("cosmetic", AiDiffNodeTask.AggregateChangeType(Array.Empty<AiDiffNodeTask.MaterialChange>(), new[] { "fmt" }));
        Assert.Equal("none", AiDiffNodeTask.AggregateChangeType(Array.Empty<AiDiffNodeTask.MaterialChange>(), Array.Empty<string>()));
    }

    [Fact]
    public void TopImpact_returns_the_highest_ranked_impact()
    {
        var changes = new[]
        {
            new AiDiffNodeTask.MaterialChange("a", "1", "2", "low"),
            new AiDiffNodeTask.MaterialChange("b", "3", "4", "high"),
            new AiDiffNodeTask.MaterialChange("c", "5", "6", "medium"),
        };
        Assert.Equal("high", AiDiffNodeTask.TopImpact(changes));
        Assert.Equal(string.Empty, AiDiffNodeTask.TopImpact(Array.Empty<AiDiffNodeTask.MaterialChange>()));
    }

    [Theory]
    [InlineData("high", "high")]
    [InlineData("CRITICAL", "high")]
    [InlineData("minor", "low")]
    [InlineData("", "medium")]
    [InlineData("weird", "medium")]
    public void NormalizeImpact_maps_onto_high_medium_low(string raw, string expected)
    {
        Assert.Equal(expected, AiDiffNodeTask.NormalizeImpact(raw));
    }

    // --- parsing ---

    [Fact]
    public void TryParseDiff_reads_material_and_ignored_and_normalizes_impact()
    {
        var json = """
            { "materialChanges": [ { "type": "price_changed", "old": "10", "new": "12", "impact": "MAJOR" } ],
              "ignoredChanges": [ "Whitespace", "" ] }
            """;
        Assert.True(AiDiffNodeTask.TryParseDiff(json, out var material, out var ignored, out _));
        var change = Assert.Single(material);
        Assert.Equal("price_changed", change.Type);
        Assert.Equal("high", change.Impact);          // normalized from "MAJOR"
        Assert.Single(ignored);                         // the empty string is dropped
        Assert.Equal("Whitespace", ignored[0]);
    }

    [Fact]
    public void TryParseDiff_tolerates_fences_and_missing_arrays()
    {
        Assert.True(AiDiffNodeTask.TryParseDiff("```json\n{}\n```", out var material, out var ignored, out _));
        Assert.Empty(material);
        Assert.Empty(ignored);
    }

    [Fact]
    public void TryParseDiff_rejects_non_object()
    {
        Assert.False(AiDiffNodeTask.TryParseDiff("not json", out _, out _, out _));
    }

    // --- malformed-output handling ---

    [Fact]
    public async Task Invalid_json_retries_once_then_succeeds()
    {
        var chat = new ScriptedChat(
            "garbage",
            """{ "materialChanges": [ { "type": "x", "old": "a", "new": "b", "impact": "low" } ], "ignoredChanges": [] }""");
        var task = new AiDiffNodeTask(chat);

        var result = await task.ExecuteAsync(Context(Inputs("a", "b")), CancellationToken.None);
        Assert.Equal("material", Assert.IsType<LegacyNodeResult.Success>(result).Outputs!["selectedPort"]);
        Assert.Equal(2, chat.Requests.Count);
        Assert.Contains("was not valid JSON", chat.Requests[1].UserMessage);
    }

    [Fact]
    public async Task Invalid_json_twice_fails_the_node()
    {
        var task = new AiDiffNodeTask(new ScriptedChat("nope", "still nope"));
        var result = await task.ExecuteAsync(Context(Inputs("a", "b")), CancellationToken.None);
        Assert.Contains("did not return a valid diff JSON", Assert.IsType<LegacyNodeResult.Failure>(result).ErrorMessage);
    }

    [Fact]
    public async Task ProviderError_becomes_a_node_failure()
    {
        var task = new AiDiffNodeTask(new UnconfiguredChatCompletionService());
        var result = await task.ExecuteAsync(Context(Inputs("a", "b")), CancellationToken.None);
        Assert.Contains("AI Semantic Diff failed", Assert.IsType<LegacyNodeResult.Failure>(result).ErrorMessage);
    }
}
