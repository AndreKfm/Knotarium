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

public class AiRouterNodeTaskTests
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
        NodeId: NodeId.Create("classify-1"),
        Inputs: inputs,
        GlobalVariables: new Dictionary<string, object>());

    private static Dictionary<string, object> BaseInputs(string categories = "Billing, Support, Spam") => new()
    {
        ["input"] = "My invoice is wrong.",
        ["categories"] = categories,
    };

    [Fact]
    public async Task MissingInput_Fails()
    {
        var task = new AiRouterNodeTask(new ScriptedChat("unused"));
        var result = await task.ExecuteAsync(Context(new() { ["categories"] = "a, b" }), CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("missing required 'input'", failure.ErrorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OnlyOne")]
    [InlineData("dup, DUP")] // dedupes case-insensitively to a single label
    public async Task FewerThanTwoCategories_Fails(string? categories)
    {
        var inputs = new Dictionary<string, object> { ["input"] = "x" };
        if (categories is not null)
        {
            inputs["categories"] = categories;
        }

        var task = new AiRouterNodeTask(new ScriptedChat("unused"));
        var result = await task.ExecuteAsync(Context(inputs), CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("at least two labels", failure.ErrorMessage);
    }

    [Fact]
    public async Task Match_RoutesTheCategoryPort_InConfiguredSpelling()
    {
        // Model answers lowercase with a period; the configured spelling is what the edges use.
        var chat = new ScriptedChat("billing.");
        var task = new AiRouterNodeTask(chat);

        var result = await task.ExecuteAsync(Context(BaseInputs()), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("Billing", success.Outputs!["selectedPort"]);
        Assert.Equal("Billing", success.Outputs!["category"]);
        Assert.Equal("billing.", success.Outputs!["reply"]);
        var request = Assert.Single(chat.Requests);
        Assert.Contains("- Billing", request.SystemPrompt);
        Assert.Contains("- Spam", request.SystemPrompt);
        Assert.Equal("My invoice is wrong.", request.UserMessage);
    }

    [Fact]
    public async Task QuotedReply_StillMatches()
    {
        var chat = new ScriptedChat("\"Spam\"");
        var task = new AiRouterNodeTask(chat);

        var result = await task.ExecuteAsync(Context(BaseInputs()), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("Spam", success.Outputs!["selectedPort"]);
    }

    [Fact]
    public async Task OffListReply_RetriesOnce_ThenMatches()
    {
        var chat = new ScriptedChat("this looks like an invoice complaint", "Billing");
        var task = new AiRouterNodeTask(chat);

        var result = await task.ExecuteAsync(Context(BaseInputs()), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("Billing", success.Outputs!["selectedPort"]);
        Assert.Equal(2, chat.Requests.Count);
        Assert.Contains("is not one of the allowed", chat.Requests[1].UserMessage);
    }

    [Fact]
    public async Task OffListTwice_RoutesOtherwise_WithoutFailing()
    {
        var chat = new ScriptedChat("no idea", "still no idea");
        var task = new AiRouterNodeTask(chat);

        var result = await task.ExecuteAsync(Context(BaseInputs()), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal(AiRouterNodeTask.OtherwisePort, success.Outputs!["selectedPort"]);
        Assert.Equal(string.Empty, success.Outputs!["category"]);
        Assert.Equal("still no idea", success.Outputs!["reply"]);
    }

    [Fact]
    public async Task Instructions_And_Overrides_ArePassedThrough()
    {
        var chat = new ScriptedChat("Support");
        var task = new AiRouterNodeTask(chat);

        var inputs = BaseInputs();
        inputs["instructions"] = "Prefer Support when a human reply is needed.";
        inputs["model"] = "claude-haiku-4-5-20251001";
        inputs["maxTokens"] = 32;

        await task.ExecuteAsync(Context(inputs), CancellationToken.None);

        var request = Assert.Single(chat.Requests);
        Assert.Contains("Prefer Support when a human reply is needed.", request.SystemPrompt);
        Assert.Equal("claude-haiku-4-5-20251001", request.Model);
        Assert.Equal(32, request.MaxTokens);
    }

    [Fact]
    public async Task ProviderError_BecomesNodeFailure()
    {
        var task = new AiRouterNodeTask(new UnconfiguredChatCompletionService());
        var result = await task.ExecuteAsync(Context(BaseInputs()), CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("AI Router failed", failure.ErrorMessage);
        Assert.Contains("not configured", failure.ErrorMessage);
    }

    [Theory]
    [InlineData("a, b, c", new[] { "a", "b", "c" })]
    [InlineData("a\nb\r\nc", new[] { "a", "b", "c" })]
    [InlineData(" a ;; b ;a", new[] { "a", "b" })]
    [InlineData("", new string[0])]
    public void ParseCategories_SplitsTrimsAndDedupes(string raw, string[] expected)
    {
        Assert.Equal(expected, AiRouterNodeTask.ParseCategories(raw));
    }
}
