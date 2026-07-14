using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.Ai;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Xunit;

namespace Knotarium.Tests.Nodes;

public class AiVerifyNodeTaskTests
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
        NodeId: NodeId.Create("verify-1"),
        Inputs: inputs,
        GlobalVariables: new Dictionary<string, object>());

    private static Dictionary<string, object> BaseInputs() => new()
    {
        ["content"] = "The camera supports AV1.",
        ["sources"] = "The camera records in H.264 and H.265. It does not support AV1.",
    };

    // --- required-input guards ---

    [Fact]
    public async Task MissingContent_Fails()
    {
        var task = new AiVerifyNodeTask(new ScriptedChat("unused"));
        var result = await task.ExecuteAsync(Context(new() { ["sources"] = "x" }), CancellationToken.None);
        Assert.Contains("missing required 'content'", Assert.IsType<LegacyNodeResult.Failure>(result).ErrorMessage);
    }

    [Fact]
    public async Task MissingSources_Fails()
    {
        var task = new AiVerifyNodeTask(new ScriptedChat("unused"));
        var result = await task.ExecuteAsync(Context(new() { ["content"] = "x" }), CancellationToken.None);
        Assert.Contains("missing required 'sources'", Assert.IsType<LegacyNodeResult.Failure>(result).ErrorMessage);
    }

    // --- routing by overall verdict ---

    [Fact]
    public async Task Contradicted_claim_routes_the_contradicted_branch()
    {
        var chat = new ScriptedChat("""
            { "claims": [ { "claim": "The camera supports AV1.", "status": "contradicted",
              "evidence": [ { "sourceId": "source-1", "passageId": "line-2", "supportsClaim": false } ] } ] }
            """);
        var task = new AiVerifyNodeTask(chat);

        var result = await task.ExecuteAsync(Context(BaseInputs()), CancellationToken.None);

        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("contradicted", success.Outputs!["selectedPort"]);
        Assert.Equal("contradicted", success.Outputs!["status"]);
        // The source id is surfaced through the system prompt so the model can cite it.
        Assert.Contains("[source-1]", chat.Requests[0].SystemPrompt);
    }

    [Fact]
    public async Task All_verified_with_support_routes_verified()
    {
        var chat = new ScriptedChat("""
            { "claims": [ { "claim": "It records H.264.", "status": "verified",
              "evidence": [ { "sourceId": "source-1", "passageId": "line-1", "supportsClaim": true } ] } ] }
            """);
        var task = new AiVerifyNodeTask(chat);

        var result = await task.ExecuteAsync(Context(BaseInputs()), CancellationToken.None);
        Assert.Equal("verified", Assert.IsType<LegacyNodeResult.Success>(result).Outputs!["selectedPort"]);
    }

    // --- the deterministic gate: the core value ---

    [Fact]
    public async Task Verified_claim_with_no_supporting_evidence_is_downgraded_to_unsupported()
    {
        // The model CLAIMS verified but cites nothing that supports it — code must not trust "probably true".
        var chat = new ScriptedChat("""
            { "claims": [ { "claim": "The camera supports AV1.", "status": "verified", "evidence": [] } ] }
            """);
        var task = new AiVerifyNodeTask(chat);

        var result = await task.ExecuteAsync(Context(BaseInputs()), CancellationToken.None);
        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.Equal("unsupported", success.Outputs!["selectedPort"]);
    }

    [Fact]
    public async Task Worst_status_wins_across_claims()
    {
        // verified + unsupported + contradicted → overall contradicted (severity aggregation).
        var claims = new List<AiVerifyNodeTask.VerifiedClaim>
        {
            new("a", AiVerifyNodeTask.Verified, Array.Empty<AiVerifyNodeTask.ClaimEvidence>()),
            new("b", AiVerifyNodeTask.Unsupported, Array.Empty<AiVerifyNodeTask.ClaimEvidence>()),
            new("c", AiVerifyNodeTask.Contradicted, Array.Empty<AiVerifyNodeTask.ClaimEvidence>()),
        };
        Assert.Equal(AiVerifyNodeTask.Contradicted, AiVerifyNodeTask.AggregateStatus(claims));
    }

    [Fact]
    public void No_claims_is_uncertain()
    {
        Assert.Equal(AiVerifyNodeTask.Uncertain,
            AiVerifyNodeTask.AggregateStatus(Array.Empty<AiVerifyNodeTask.VerifiedClaim>()));
    }

    [Theory]
    [InlineData("verified", "verified")]
    [InlineData("SUPPORTED", "verified")]
    [InlineData("refuted", "contradicted")]
    [InlineData("no evidence", "unsupported")]
    [InlineData("indeterminate", "uncertain")]
    [InlineData("banana", "uncertain")]
    [InlineData("", "uncertain")]
    public void NormalizeStatus_maps_onto_the_fixed_vocabulary(string raw, string expected)
    {
        Assert.Equal(expected, AiVerifyNodeTask.NormalizeStatus(raw));
    }

    // --- evidence-rule enforcement inside parsing ---

    [Fact]
    public void TryParseClaims_downgrades_unsupported_and_keeps_contradicting_evidence()
    {
        var json = """
            { "claims": [
              { "claim": "supported one", "status": "verified", "evidence": [ { "sourceId": "s1", "passageId": "p1", "supportsClaim": true } ] },
              { "claim": "bare verified", "status": "verified", "evidence": [] },
              { "claim": "contra", "status": "contradicted", "evidence": [ { "sourceId": "s1", "passageId": "p2", "supportsClaim": false } ] }
            ] }
            """;
        Assert.True(AiVerifyNodeTask.TryParseClaims(json, out var claims, out _));
        Assert.Equal(3, claims.Count);
        Assert.Equal(AiVerifyNodeTask.Verified, claims[0].Status);
        Assert.Equal(AiVerifyNodeTask.Unsupported, claims[1].Status);   // downgraded
        Assert.Equal(AiVerifyNodeTask.Contradicted, claims[2].Status);
        Assert.False(claims[2].Evidence[0].SupportsClaim);
    }

    [Fact]
    public void TryParseClaims_tolerates_a_fenced_reply()
    {
        var fenced = "```json\n{ \"claims\": [] }\n```";
        Assert.True(AiVerifyNodeTask.TryParseClaims(fenced, out var claims, out _));
        Assert.Empty(claims);
    }

    [Fact]
    public void TryParseClaims_rejects_non_object_and_missing_claims()
    {
        Assert.False(AiVerifyNodeTask.TryParseClaims("not json", out _, out _));
        Assert.False(AiVerifyNodeTask.TryParseClaims("{ \"foo\": 1 }", out _, out _));
    }

    // --- sources normalization ---

    [Fact]
    public void NormalizeSources_reads_a_json_array_with_ids()
    {
        var sources = AiVerifyNodeTask.NormalizeSources("""[ { "id": "manual-17", "content": "AV1 unsupported" }, { "text": "no id here" } ]""");
        Assert.Equal(2, sources.Count);
        Assert.Equal("manual-17", sources[0].Id);
        Assert.Equal("AV1 unsupported", sources[0].Content);
        Assert.Equal("source-2", sources[1].Id);   // fallback id when none given
    }

    [Fact]
    public void NormalizeSources_treats_plain_text_as_one_source()
    {
        var sources = AiVerifyNodeTask.NormalizeSources("just some reference text");
        var single = Assert.Single(sources);
        Assert.Equal("source-1", single.Id);
        Assert.Equal("just some reference text", single.Content);
    }

    // --- malformed-output handling ---

    [Fact]
    public async Task Invalid_json_retries_once_then_succeeds()
    {
        var chat = new ScriptedChat(
            "not json at all",
            """{ "claims": [ { "claim": "x", "status": "unsupported", "evidence": [] } ] }""");
        var task = new AiVerifyNodeTask(chat);

        var result = await task.ExecuteAsync(Context(BaseInputs()), CancellationToken.None);
        Assert.Equal("unsupported", Assert.IsType<LegacyNodeResult.Success>(result).Outputs!["selectedPort"]);
        Assert.Equal(2, chat.Requests.Count);
        Assert.Contains("was not valid JSON", chat.Requests[1].UserMessage);
    }

    [Fact]
    public async Task Invalid_json_twice_fails_the_node()
    {
        var task = new AiVerifyNodeTask(new ScriptedChat("nope", "still nope"));
        var result = await task.ExecuteAsync(Context(BaseInputs()), CancellationToken.None);
        Assert.Contains("did not return a valid claims JSON", Assert.IsType<LegacyNodeResult.Failure>(result).ErrorMessage);
    }

    [Fact]
    public async Task ProviderError_becomes_a_node_failure()
    {
        var task = new AiVerifyNodeTask(new UnconfiguredChatCompletionService());
        var result = await task.ExecuteAsync(Context(BaseInputs()), CancellationToken.None);
        Assert.Contains("AI Verify failed", Assert.IsType<LegacyNodeResult.Failure>(result).ErrorMessage);
    }
}
