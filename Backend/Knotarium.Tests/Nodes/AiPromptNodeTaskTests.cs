// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.Ai;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Xunit;

namespace Knotarium.Tests.Nodes;

public class AiPromptNodeTaskTests
{
    /// <summary>Replays scripted replies in order and records every request for assertions.</summary>
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

    private sealed class ThrowingChat : IChatCompletionService
    {
        public Task<string> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("The AI provider is not configured.");
    }

    private static NodeExecutionContext Context(Dictionary<string, object> inputs) => new(
        WorkflowId: WorkflowDefinitionId.New(),
        ExecutionId: Guid.NewGuid(),
        NodeId: NodeId.Create("ai-1"),
        Inputs: inputs,
        GlobalVariables: new Dictionary<string, object>());

    [Fact]
    public async Task MissingPrompt_Fails()
    {
        var task = new AiPromptNodeTask(new ScriptedChat("unused"));
        var result = await task.ExecuteAsync(Context(new()), CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("missing required 'prompt'", failure.ErrorMessage);
    }

    [Fact]
    public async Task TextMode_EmitsReplyOnResult_AndAppliesDefaultSystemPrompt()
    {
        var chat = new ScriptedChat("Bonjour");
        var task = new AiPromptNodeTask(chat);

        var result = await task.ExecuteAsync(
            Context(new() { ["prompt"] = "Translate 'Hello' to French." }), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("Bonjour", success.Outputs!["result"]);
        var request = Assert.Single(chat.Requests);
        Assert.Equal(AiPromptNodeTask.DefaultSystemPrompt, request.SystemPrompt);
        Assert.Equal("Translate 'Hello' to French.", request.UserMessage);
        Assert.Null(request.Model);
        Assert.Null(request.MaxTokens);
    }

    [Fact]
    public async Task Overrides_ArePassedThrough()
    {
        var chat = new ScriptedChat("ok");
        var task = new AiPromptNodeTask(chat);

        await task.ExecuteAsync(Context(new()
        {
            ["prompt"] = "p",
            ["systemPrompt"] = "You are a pirate.",
            ["model"] = "claude-sonnet-5",
            ["maxTokens"] = 512,
        }), CancellationToken.None);

        var request = Assert.Single(chat.Requests);
        Assert.Equal("You are a pirate.", request.SystemPrompt);
        Assert.Equal("claude-sonnet-5", request.Model);
        Assert.Equal(512, request.MaxTokens);
    }

    [Fact]
    public async Task JsonMode_ParsesReply_AndAppendsSchemaInstruction()
    {
        var chat = new ScriptedChat("""{ "sentiment": "positive" }""");
        var task = new AiPromptNodeTask(chat);

        var result = await task.ExecuteAsync(Context(new()
        {
            ["prompt"] = "Classify.",
            ["jsonSchema"] = """{ "type": "object", "properties": { "sentiment": { "type": "string" } } }""",
        }), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        var parsed = Assert.IsType<JsonElement>(success.Outputs!["result"]);
        Assert.Equal("positive", parsed.GetProperty("sentiment").GetString());
        var request = Assert.Single(chat.Requests);
        Assert.Contains("JSON schema", request.SystemPrompt);
        Assert.Contains("\"sentiment\"", request.SystemPrompt);
    }

    [Fact]
    public async Task JsonMode_ToleratesFencedReply()
    {
        var chat = new ScriptedChat("```json\n{ \"ok\": true }\n```");
        var task = new AiPromptNodeTask(chat);

        var result = await task.ExecuteAsync(Context(new()
        {
            ["prompt"] = "p",
            ["jsonSchema"] = "{}",
        }), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        var parsed = Assert.IsType<JsonElement>(success.Outputs!["result"]);
        Assert.True(parsed.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task JsonMode_InvalidReply_RepairsOnce_WithErrorFeedback()
    {
        var chat = new ScriptedChat("this is not JSON", """{ "fixed": true }""");
        var task = new AiPromptNodeTask(chat);

        var result = await task.ExecuteAsync(Context(new()
        {
            ["prompt"] = "p",
            ["jsonSchema"] = "{}",
        }), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        var parsed = Assert.IsType<JsonElement>(success.Outputs!["result"]);
        Assert.True(parsed.GetProperty("fixed").GetBoolean());
        Assert.Equal(2, chat.Requests.Count);
        Assert.Contains("was not valid JSON", chat.Requests[1].UserMessage);
        Assert.Contains("this is not JSON", chat.Requests[1].UserMessage);
    }

    [Fact]
    public async Task JsonMode_InvalidTwice_Fails()
    {
        var chat = new ScriptedChat("nope", "still nope");
        var task = new AiPromptNodeTask(chat);

        var result = await task.ExecuteAsync(Context(new()
        {
            ["prompt"] = "p",
            ["jsonSchema"] = "{}",
        }), CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("did not return valid JSON after a retry", failure.ErrorMessage);
    }

    [Fact]
    public async Task ProviderError_BecomesNodeFailure()
    {
        var task = new AiPromptNodeTask(new ThrowingChat());
        var result = await task.ExecuteAsync(
            Context(new() { ["prompt"] = "p" }), CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("AI prompt failed", failure.ErrorMessage);
        Assert.Contains("not configured", failure.ErrorMessage);
    }

    [Fact]
    public async Task UnconfiguredFallback_YieldsClearNodeFailure()
    {
        // The TryAdd default a barebone host gets when the AI slice is not registered.
        var task = new AiPromptNodeTask(new UnconfiguredChatCompletionService());
        var result = await task.ExecuteAsync(
            Context(new() { ["prompt"] = "p" }), CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("Settings → AI Provider", failure.ErrorMessage);
    }
}
