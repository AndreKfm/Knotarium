// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.Ai;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Evidence gate: verifies the factual claims in <c>content</c> against the supplied <c>sources</c>,
/// claim-by-claim, and routes the run down a fixed branch by the overall verdict
/// (<c>verified</c> / <c>unsupported</c> / <c>contradicted</c> / <c>uncertain</c>).
///
/// This is deliberately more than "ask a second LLM if it looks right". The model only does what
/// models are good at — extract each claim and map it to source passages — and returns structured
/// JSON. The <b>gate itself is deterministic code</b>:
/// <list type="bullet">
///   <item>the evidence rule is enforced in code — a claim the model calls "verified" but backs with
///   no supporting evidence is downgraded to <c>unsupported</c> (no evidence ≠ probably true);</item>
///   <item>the overall verdict is computed by severity (contradicted &gt; unsupported &gt; uncertain &gt;
///   verified), not taken from a model-authored summary label.</item>
/// </list>
/// The full structured result (per-claim status + cited evidence + the model + prompt version) is
/// emitted on <c>result</c>; because the engine journals node outputs, that record is the auditable,
/// replayable verification trail.
/// </summary>
public class AiVerifyNodeTask : INodeTask
{
    /// <summary>Bumped when the verification instructions change, so the audit record pins which prompt judged.</summary>
    internal const string PromptVersion = "verify-1";

    private readonly IChatCompletionService _chat;

    public AiVerifyNodeTask(IChatCompletionService chat) => _chat = chat;

    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var content = Input(context, "content");
        if (string.IsNullOrWhiteSpace(content))
        {
            return new LegacyNodeResult.Failure("AI Verify failed: missing required 'content' (the text whose claims to verify).");
        }

        var sourcesRaw = Input(context, "sources");
        if (string.IsNullOrWhiteSpace(sourcesRaw))
        {
            return new LegacyNodeResult.Failure("AI Verify failed: missing required 'sources' (the reference material to check against).");
        }

        var sources = NormalizeSources(sourcesRaw!);
        var instructions = Input(context, "instructions");
        var model = Input(context, "model");
        int? maxTokens = int.TryParse(Input(context, "maxTokens"), out var parsedMax) && parsedMax > 0 ? parsedMax : null;

        var systemPrompt = BuildSystemPrompt(sources, instructions);
        var userMessage = "Verify the factual claims in the following content against the sources above.\n\nCONTENT:\n" + content;

        try
        {
            var reply = await _chat.CompleteAsync(
                new ChatCompletionRequest(systemPrompt, userMessage, model, maxTokens), cancellationToken);

            if (!TryParseClaims(reply, out var claims, out var parseError))
            {
                // One repair pass: the schema was probably close; feed the error back once.
                var repair = userMessage +
                    $"\n\nYour previous reply was not valid JSON in the required shape.\nReply:\n{reply}\nError: {parseError}\n" +
                    "Answer again with ONLY the JSON object.";
                reply = await _chat.CompleteAsync(
                    new ChatCompletionRequest(systemPrompt, repair, model, maxTokens), cancellationToken);
                if (!TryParseClaims(reply, out claims, out parseError))
                {
                    return new LegacyNodeResult.Failure($"AI Verify failed: the model did not return a valid claims JSON after a retry ({parseError}).");
                }
            }

            var overall = AggregateStatus(claims);

            var result = new Dictionary<string, object>
            {
                ["status"] = overall,
                ["model"] = model ?? string.Empty,
                ["promptVersion"] = PromptVersion,
                ["claims"] = claims.Select(c => c.ToMap()).ToList(),
            };

            return new LegacyNodeResult.Success(new Dictionary<string, object>
            {
                ["selectedPort"] = overall,        // routes to the matching fixed branch
                ["status"] = overall,
                ["claims"] = claims.Select(c => c.ToMap()).ToList(),
                ["result"] = result,               // the auditable/replayable record (journaled)
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"AI Verify failed: {ex.Message}");
        }
    }

    // --- Verdict vocabulary ---

    internal const string Verified = "verified";
    internal const string Unsupported = "unsupported";
    internal const string Contradicted = "contradicted";
    internal const string Uncertain = "uncertain";

    // Severity order for aggregating the overall verdict: the worst signal present wins. A single
    // contradiction is the most important thing to surface; an all-verified set is the only "pass".
    private static int Severity(string status) => status switch
    {
        Contradicted => 3,
        Unsupported => 2,
        Uncertain => 1,
        _ => 0, // verified
    };

    /// <summary>
    /// The overall verdict = the highest-severity per-claim status. No claims at all is treated as
    /// <c>uncertain</c> (we verified nothing, so we can't pass it).
    /// </summary>
    internal static string AggregateStatus(IReadOnlyList<VerifiedClaim> claims)
    {
        if (claims.Count == 0)
        {
            return Uncertain;
        }

        var worst = Verified;
        foreach (var claim in claims)
        {
            if (Severity(claim.Status) > Severity(worst))
            {
                worst = claim.Status;
            }
        }
        return worst;
    }

    /// <summary>Maps a model-reported status onto the fixed vocabulary; unknown/blank → uncertain.</summary>
    internal static string NormalizeStatus(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "verified" or "supported" or "support" or "true" or "correct" => Verified,
        "contradicted" or "refuted" or "false" or "incorrect" => Contradicted,
        "unsupported" or "no evidence" or "none" or "unverifiable" => Unsupported,
        _ => Uncertain, // "indeterminate", "unknown", blank, or anything unexpected
    };

    // --- Parsing ---

    /// <summary>A single verified claim after code enforcement of the evidence rule.</summary>
    internal sealed record VerifiedClaim(string Claim, string Status, IReadOnlyList<ClaimEvidence> Evidence)
    {
        public Dictionary<string, object> ToMap() => new()
        {
            ["claim"] = Claim,
            ["status"] = Status,
            ["evidence"] = Evidence.Select(e => e.ToMap()).ToList(),
        };
    }

    internal sealed record ClaimEvidence(string SourceId, string PassageId, bool SupportsClaim)
    {
        public Dictionary<string, object> ToMap() => new()
        {
            ["sourceId"] = SourceId,
            ["passageId"] = PassageId,
            ["supportsClaim"] = SupportsClaim,
        };
    }

    internal static bool TryParseClaims(string reply, out List<VerifiedClaim> claims, out string? error)
    {
        claims = new List<VerifiedClaim>();
        error = null;

        if (!TryParseJsonObject(reply, out var root, out error))
        {
            return false;
        }

        if (!root.TryGetProperty("claims", out var claimsEl) || claimsEl.ValueKind != JsonValueKind.Array)
        {
            error = "response had no 'claims' array";
            return false;
        }

        foreach (var claimEl in claimsEl.EnumerateArray())
        {
            var text = claimEl.TryGetProperty("claim", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? string.Empty
                : string.Empty;

            var evidence = new List<ClaimEvidence>();
            if (claimEl.TryGetProperty("evidence", out var evEl) && evEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in evEl.EnumerateArray())
                {
                    evidence.Add(new ClaimEvidence(
                        Str(e, "sourceId"),
                        Str(e, "passageId"),
                        e.TryGetProperty("supportsClaim", out var s) && s.ValueKind == JsonValueKind.True));
                }
            }

            var status = NormalizeStatus(claimEl.TryGetProperty("status", out var st) ? st.GetString() : null);

            // Evidence rule, enforced in code: a claim can only be "verified" if at least one piece of
            // evidence actually supports it. Otherwise it is unsupported — never "probably true".
            if (status == Verified && !evidence.Any(e => e.SupportsClaim))
            {
                status = Unsupported;
            }

            claims.Add(new VerifiedClaim(text, status, evidence));
        }

        return true;
    }

    private static string Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    /// <summary>Fence-tolerant parse of the model reply into a JSON object root.</summary>
    private static bool TryParseJsonObject(string reply, out JsonElement root, out string? error)
    {
        root = default;
        error = null;

        var text = reply.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                text = text[(firstNewline + 1)..lastFence].Trim();
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "response was not a JSON object";
                return false;
            }
            root = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // --- Sources ---

    /// <summary>A reference source the claims are checked against.</summary>
    internal sealed record Source(string Id, string Content);

    /// <summary>
    /// Accepts <c>sources</c> as either a JSON array of <c>{ id, content|text }</c> objects (so the model
    /// can cite a real sourceId), or plain text (treated as one source with id <c>source-1</c>).
    /// </summary>
    internal static List<Source> NormalizeSources(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<Source>();
                    var i = 0;
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        i++;
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            list.Add(new Source($"source-{i}", el.GetString() ?? string.Empty));
                            continue;
                        }
                        var id = Str(el, "id");
                        if (string.IsNullOrWhiteSpace(id)) id = Str(el, "sourceId");
                        if (string.IsNullOrWhiteSpace(id)) id = $"source-{i}";
                        var body = Str(el, "content");
                        if (string.IsNullOrWhiteSpace(body)) body = Str(el, "text");
                        list.Add(new Source(id, body));
                    }
                    if (list.Count > 0) return list;
                }
            }
            catch (JsonException)
            {
                // Not JSON after all — fall through to single plain-text source.
            }
        }

        return new List<Source> { new("source-1", trimmed) };
    }

    private static string BuildSystemPrompt(IReadOnlyList<Source> sources, string? instructions)
    {
        var sb = new StringBuilder();
        sb.Append(
            "You are an evidence-based fact-checking engine inside an automation workflow. Verify the " +
            "content's factual claims ONLY against the sources provided below — never against your own " +
            "prior knowledge. Extract each distinct factual claim and judge it independently.\n\n");

        sb.Append("Rules:\n");
        sb.Append("- A claim is \"verified\" ONLY if a source passage directly supports it.\n");
        sb.Append("- A claim is \"contradicted\" if a source passage states the opposite.\n");
        sb.Append("- A claim is \"unsupported\" if NO source addresses it. No evidence means unsupported — never assume it is probably true.\n");
        sb.Append("- A claim is \"uncertain\" only if the sources are genuinely ambiguous about it.\n");
        sb.Append("- Cite the sourceId and a short passageId (e.g. a heading, line, or section) for every piece of evidence, and whether it supports the claim.\n\n");

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            sb.Append("Additional guidance: ").Append(instructions).Append("\n\n");
        }

        sb.Append("SOURCES:\n");
        foreach (var s in sources)
        {
            sb.Append('[').Append(s.Id).Append("]\n").Append(s.Content).Append("\n\n");
        }

        sb.Append(
            "Respond with ONLY this JSON object (no markdown fences, no commentary):\n" +
            "{ \"claims\": [ { \"claim\": \"<the claim>\", \"status\": \"verified|contradicted|unsupported|uncertain\", " +
            "\"evidence\": [ { \"sourceId\": \"<id>\", \"passageId\": \"<where>\", \"supportsClaim\": true|false } ] } ] }");

        return sb.ToString();
    }

    private static string? Input(NodeExecutionContext context, string key)
        => context.Inputs.TryGetValue(key, out var value) ? value?.ToString() : null;
}
