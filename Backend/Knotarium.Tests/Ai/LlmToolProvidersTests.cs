// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.Ai;
using Knotarium.Features.Ai.Providers;
using Xunit;

namespace Knotarium.Tests.Ai;

/// <summary>
/// Contract tests for the tool-calling adapters: request-body/URL/auth mapping of the neutral transcript
/// + tool definitions, and response parsing back to an <see cref="AgentTurnResult"/> (text vs tool calls,
/// token usage). Uses a capturing HTTP handler so no network is touched.
/// </summary>
public class LlmToolProvidersTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseBody;
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        public CapturingHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_responseBody) };
        }
    }

    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public FakeFactory(HttpMessageHandler handler) => _client = new HttpClient(handler);
        public HttpClient CreateClient(string name) => _client;
    }

    private static JsonElement Schema(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static JsonElement Json(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static readonly IReadOnlyList<AgentToolDefinition> OneTool = new[]
    {
        new AgentToolDefinition("lookup", "look up a customer",
            Schema("""{ "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] }""")),
    };

    private static LlmToolChatRequest Req(AiProviderConfig config, IReadOnlyList<AgentMessage> messages) =>
        new("SYSTEM", messages, OneTool, config, "sk-key", 4096);

    // --- Anthropic ---

    [Fact]
    public async Task Anthropic_maps_tools_and_transcript_and_parses_tool_use()
    {
        var handler = new CapturingHandler("""
            { "content": [ { "type": "text", "text": "let me look" },
                           { "type": "tool_use", "id": "toolu_1", "name": "lookup", "input": { "id": "c-7" } } ],
              "stop_reason": "tool_use", "usage": { "input_tokens": 11, "output_tokens": 7 } }
            """);
        var provider = new AnthropicToolChatProvider(new FakeFactory(handler));
        var config = new AiProviderConfig(LlmVendors.Anthropic, "claude-opus-4-8", "cred-1");

        var messages = new List<AgentMessage>
        {
            new(AgentRoles.User, "look up c-7"),
            new(AgentRoles.Assistant, "sure", new[] { new AgentToolCall("toolu_0", "lookup", Json("""{ "id": "c-6" }""")) }),
            new(AgentRoles.Tool, ToolCallId: "toolu_0", ToolResultJson: """{ "customer": "Old" }"""),
        };

        var turn = await provider.CompleteTurnAsync(Req(config, messages), CancellationToken.None);

        // Response parsing.
        Assert.Equal("let me look", turn.FinalText);
        var call = Assert.Single(turn.ToolCalls);
        Assert.Equal("lookup", call.Name);
        Assert.Equal("c-7", call.Arguments.GetProperty("id").GetString());
        Assert.Equal(11, turn.InputTokens);
        Assert.Equal(7, turn.OutputTokens);

        // Request mapping: tools with input_schema; tool_use/tool_result blocks present.
        Assert.EndsWith("/v1/messages", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.True(handler.Request.Headers.Contains("x-api-key"));
        Assert.Contains("input_schema", handler.RequestBody);
        Assert.Contains("tool_use", handler.RequestBody);
        Assert.Contains("tool_result", handler.RequestBody);
    }

    [Fact]
    public async Task Anthropic_text_only_is_a_final_answer()
    {
        var handler = new CapturingHandler("""
            { "content": [ { "type": "text", "text": "the answer" } ], "stop_reason": "end_turn",
              "usage": { "input_tokens": 3, "output_tokens": 2 } }
            """);
        var provider = new AnthropicToolChatProvider(new FakeFactory(handler));
        var config = new AiProviderConfig(LlmVendors.Anthropic, "claude-opus-4-8", "cred-1");

        var turn = await provider.CompleteTurnAsync(
            Req(config, new[] { new AgentMessage(AgentRoles.User, "q") }), CancellationToken.None);

        Assert.Equal("the answer", turn.FinalText);
        Assert.Empty(turn.ToolCalls);
    }

    // --- OpenAI ---

    [Fact]
    public async Task OpenAi_maps_tools_and_parses_tool_calls_with_string_arguments()
    {
        var handler = new CapturingHandler("""
            { "choices": [ { "message": { "content": null, "tool_calls": [
                { "id": "call_1", "type": "function", "function": { "name": "lookup", "arguments": "{\"id\":\"c-9\"}" } } ] },
                "finish_reason": "tool_calls" } ],
              "usage": { "prompt_tokens": 20, "completion_tokens": 4 } }
            """);
        var provider = new OpenAiToolChatProvider(new FakeFactory(handler));
        var config = new AiProviderConfig(LlmVendors.OpenAi, "gpt-4o", "cred-1");

        var turn = await provider.CompleteTurnAsync(
            Req(config, new[] { new AgentMessage(AgentRoles.User, "look up c-9") }), CancellationToken.None);

        Assert.Null(turn.FinalText);
        var call = Assert.Single(turn.ToolCalls);
        Assert.Equal("lookup", call.Name);
        Assert.Equal("c-9", call.Arguments.GetProperty("id").GetString());
        Assert.Equal(20, turn.InputTokens);

        Assert.EndsWith("/v1/chat/completions", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Contains("\"tools\"", handler.RequestBody);
        Assert.Contains("\"type\":\"function\"", handler.RequestBody);
    }

    [Fact]
    public async Task OpenAi_maps_assistant_toolcall_and_tool_result_messages()
    {
        var handler = new CapturingHandler("""{ "choices": [ { "message": { "content": "done" }, "finish_reason": "stop" } ] }""");
        var provider = new OpenAiToolChatProvider(new FakeFactory(handler));
        var config = new AiProviderConfig(LlmVendors.OpenAi, "gpt-4o", "cred-1");

        var messages = new List<AgentMessage>
        {
            new(AgentRoles.User, "go"),
            new(AgentRoles.Assistant, null, new[] { new AgentToolCall("call_1", "lookup", Json("""{ "id": "x" }""")) }),
            new(AgentRoles.Tool, ToolCallId: "call_1", ToolResultJson: """{ "customer": "Acme" }"""),
        };

        var turn = await provider.CompleteTurnAsync(Req(config, messages), CancellationToken.None);

        Assert.Equal("done", turn.FinalText);
        Assert.Empty(turn.ToolCalls);
        Assert.Contains("\"role\":\"tool\"", handler.RequestBody);
        Assert.Contains("\"tool_call_id\":\"call_1\"", handler.RequestBody);
        Assert.Contains("\"tool_calls\"", handler.RequestBody);
    }

    [Fact]
    public async Task Azure_builds_deployment_url_and_maps_tools()
    {
        var handler = new CapturingHandler("""{ "choices": [ { "message": { "content": "hi" }, "finish_reason": "stop" } ] }""");
        var provider = new AzureOpenAiToolChatProvider(new FakeFactory(handler));
        var config = new AiProviderConfig(LlmVendors.Azure, "my-deploy", "cred-1", BaseUrl: "https://res.openai.azure.com");

        var turn = await provider.CompleteTurnAsync(
            Req(config, new[] { new AgentMessage(AgentRoles.User, "q") }), CancellationToken.None);

        Assert.Equal("hi", turn.FinalText);
        Assert.Contains("/openai/deployments/my-deploy/chat/completions", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.True(handler.Request.Headers.Contains("api-key"));
        Assert.Contains("\"tools\"", handler.RequestBody);
    }

    [Fact]
    public async Task NonSuccessStatus_throws()
    {
        var handler = new CapturingHandler("""{ "error": "bad" }""", HttpStatusCode.Unauthorized);
        var provider = new OpenAiToolChatProvider(new FakeFactory(handler));
        var config = new AiProviderConfig(LlmVendors.OpenAi, "gpt-4o", "cred-1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteTurnAsync(Req(config, new[] { new AgentMessage(AgentRoles.User, "q") }), CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }

    // Returns a scripted sequence of (status, body) responses and records every request body.
    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;
        public List<string> RequestBodies { get; } = new();
        public SequencedHandler(params (HttpStatusCode, string)[] responses) => _responses = new Queue<(HttpStatusCode, string)>(responses);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body) = _responses.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    [Fact]
    public async Task OpenAi_retries_with_reasoning_none_on_reasoning_model_400()
    {
        // A reasoning model rejects function tools on the first call; the provider must retry once with
        // reasoning_effort:"none" and succeed. Uses a unique model name so the static cache doesn't collide.
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, """{ "error": { "message": "Function tools with reasoning_effort are not supported for reasoning-test-model-A in /v1/chat/completions. Set reasoning_effort to 'none'." } }"""),
            (HttpStatusCode.OK, """{ "choices": [ { "message": { "content": "done" }, "finish_reason": "stop" } ] }"""));
        var provider = new OpenAiToolChatProvider(new FakeFactory(handler));
        var config = new AiProviderConfig(LlmVendors.OpenAi, "reasoning-test-model-A", "cred-1");

        var turn = await provider.CompleteTurnAsync(
            Req(config, new[] { new AgentMessage(AgentRoles.User, "q") }), CancellationToken.None);

        Assert.Equal("done", turn.FinalText);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.DoesNotContain("reasoning_effort", handler.RequestBodies[0]); // first attempt: not sent
        Assert.Contains("\"reasoning_effort\":\"none\"", handler.RequestBodies[1]); // retry: sent
    }

    [Fact]
    public async Task OpenAi_reasoning_model_that_still_fails_tools_gives_an_actionable_message()
    {
        // Reasoning model: first turn 400 (reasoning_effort) → retry with none → still fails (e.g. 401).
        // The operator should get the "use a tool-capable model" guidance, not a raw HTTP error.
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, """{ "error": { "message": "Function tools with reasoning_effort are not supported for reasoning-test-model-B. Set reasoning_effort to 'none'." } }"""),
            (HttpStatusCode.Unauthorized, """{ "error": { "message": "You have insufficient permissions for this operation." } }"""));
        var provider = new OpenAiToolChatProvider(new FakeFactory(handler));
        var config = new AiProviderConfig(LlmVendors.OpenAi, "reasoning-test-model-B", "cred-1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteTurnAsync(Req(config, new[] { new AgentMessage(AgentRoles.User, "q") }), CancellationToken.None));
        Assert.Contains("tool-capable model", ex.Message);
        Assert.Equal(2, handler.RequestBodies.Count); // it did try the reasoning_effort:none retry
    }

    [Fact]
    public async Task OpenAi_does_not_add_reasoning_effort_for_a_normal_400()
    {
        // A 400 that is NOT about reasoning_effort must not trigger the retry (no false positives).
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, """{ "error": { "message": "Unsupported value for 'temperature'." } }"""));
        var provider = new OpenAiToolChatProvider(new FakeFactory(handler));
        var config = new AiProviderConfig(LlmVendors.OpenAi, "gpt-4o-normal-400", "cred-1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteTurnAsync(Req(config, new[] { new AgentMessage(AgentRoles.User, "q") }), CancellationToken.None));
        Assert.Single(handler.RequestBodies); // no retry
    }
}
