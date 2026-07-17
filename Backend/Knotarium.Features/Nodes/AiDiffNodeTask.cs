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
/// Semantic diff: compares the <c>previous</c> and <c>current</c> versions of a document by MEANING
/// (not character-by-character) and routes the run by whether anything meaningful actually changed —
/// <c>material</c> / <c>cosmetic</c> / <c>none</c>.
///
/// Like <see cref="AiVerifyNodeTask"/>, the model only does the linguistic work (find the changes and
/// separate meaningful ones from formatting/whitespace/synonym rewrites) and returns structured JSON;
/// the routing verdict is <b>deterministic code</b>:
/// <list type="bullet">
///   <item>byte-identical inputs short-circuit to <c>none</c> without spending an LLM call;</item>
///   <item>any material change → <c>material</c>; else changes-but-only-cosmetic → <c>cosmetic</c>;
///   else <c>none</c> — computed from the parsed lists, not from a model-authored label.</item>
/// </list>
/// Emits the full record (material + ignored changes, the highest impact, model, prompt version) on
/// <c>result</c>; the engine journals node outputs, so that is the auditable change record.
/// </summary>
public class AiDiffNodeTask : INodeTask
{
    internal const string PromptVersion = "diff-1";

    internal const string Material = "material";
    internal const string Cosmetic = "cosmetic";
    internal const string None = "none";

    private readonly IChatCompletionService _chat;

    public AiDiffNodeTask(IChatCompletionService chat) => _chat = chat;

    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var previous = Input(context, "previous");
        var current = Input(context, "current");
        if (previous is null || current is null)
        {
            return new LegacyNodeResult.Failure("AI Semantic Diff failed: both 'previous' and 'current' are required.");
        }

        // Deterministic short-circuit: identical content (ignoring only surrounding whitespace) can never
        // be a change, so route 'none' without an LLM call.
        if (string.Equals(previous.Trim(), current.Trim(), StringComparison.Ordinal))
        {
            return Success(None, new List<MaterialChange>(), new List<string>(), Input(context, "model"));
        }

        var instructions = Input(context, "instructions");
        var model = Input(context, "model");
        int? maxTokens = int.TryParse(Input(context, "maxTokens"), out var parsedMax) && parsedMax > 0 ? parsedMax : null;

        var systemPrompt = BuildSystemPrompt(instructions);
        var userMessage = $"PREVIOUS version:\n{previous}\n\nCURRENT version:\n{current}";

        try
        {
            var reply = await _chat.CompleteAsync(
                new ChatCompletionRequest(systemPrompt, userMessage, model, maxTokens), cancellationToken);

            if (!TryParseDiff(reply, out var material, out var ignored, out var parseError))
            {
                var repair = userMessage +
                    $"\n\nYour previous reply was not valid JSON in the required shape.\nReply:\n{reply}\nError: {parseError}\n" +
                    "Answer again with ONLY the JSON object.";
                reply = await _chat.CompleteAsync(
                    new ChatCompletionRequest(systemPrompt, repair, model, maxTokens), cancellationToken);
                if (!TryParseDiff(reply, out material, out ignored, out parseError))
                {
                    return new LegacyNodeResult.Failure($"AI Semantic Diff failed: the model did not return a valid diff JSON after a retry ({parseError}).");
                }
            }

            var changeType = AggregateChangeType(material, ignored);
            return Success(changeType, material, ignored, model);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"AI Semantic Diff failed: {ex.Message}");
        }
    }

    private static LegacyNodeResult Success(string changeType, IReadOnlyList<MaterialChange> material, IReadOnlyList<string> ignored, string? model)
    {
        var result = new Dictionary<string, object>
        {
            ["changeType"] = changeType,
            ["impact"] = TopImpact(material),
            ["model"] = model ?? string.Empty,
            ["promptVersion"] = PromptVersion,
            ["materialChanges"] = material.Select(m => m.ToMap()).ToList(),
            ["ignoredChanges"] = ignored.ToList(),
        };

        return new LegacyNodeResult.Success(new Dictionary<string, object>
        {
            ["selectedPort"] = changeType,     // routes material / cosmetic / none
            ["changeType"] = changeType,
            ["materialChanges"] = material.Select(m => m.ToMap()).ToList(),
            ["result"] = result,               // the auditable change record (journaled)
        });
    }

    /// <summary>
    /// Deterministic verdict: any material change wins; otherwise changes that exist but are all
    /// cosmetic → <c>cosmetic</c>; nothing at all → <c>none</c>.
    /// </summary>
    internal static string AggregateChangeType(IReadOnlyList<MaterialChange> material, IReadOnlyList<string> ignored)
    {
        if (material.Count > 0) return Material;
        if (ignored.Count > 0) return Cosmetic;
        return None;
    }

    // --- impact ---

    private static int ImpactRank(string impact) => impact switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0,
    };

    /// <summary>Highest impact among the material changes ("high"/"medium"/"low"), or "" when none.</summary>
    internal static string TopImpact(IReadOnlyList<MaterialChange> material)
    {
        var top = string.Empty;
        foreach (var m in material)
        {
            if (ImpactRank(m.Impact) > ImpactRank(top))
            {
                top = m.Impact;
            }
        }
        return top;
    }

    internal static string NormalizeImpact(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "high" or "critical" or "major" => "high",
        "low" or "minor" or "trivial" => "low",
        "" => "medium",
        _ => "medium",
    };

    // --- parsing ---

    internal sealed record MaterialChange(string Type, string Old, string New, string Impact)
    {
        public Dictionary<string, object> ToMap() => new()
        {
            ["type"] = Type,
            ["old"] = Old,
            ["new"] = New,
            ["impact"] = Impact,
        };
    }

    internal static bool TryParseDiff(string reply, out List<MaterialChange> material, out List<string> ignored, out string? error)
    {
        material = new List<MaterialChange>();
        ignored = new List<string>();
        error = null;

        if (!TryParseJsonObject(reply, out var root, out error))
        {
            return false;
        }

        // Both arrays are optional in the wire shape (a no-change reply may omit them); default to empty.
        if (root.TryGetProperty("materialChanges", out var mEl) && mEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in mEl.EnumerateArray())
            {
                material.Add(new MaterialChange(
                    Str(el, "type"),
                    Str(el, "old"),
                    Str(el, "new"),
                    NormalizeImpact(el.TryGetProperty("impact", out var i) ? i.GetString() : null)));
            }
        }

        if (root.TryGetProperty("ignoredChanges", out var iEl) && iEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in iEl.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) ignored.Add(s!);
                }
            }
        }

        // A well-formed object with neither array is still a valid "no change" answer.
        return true;
    }

    private static string Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

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

    private static string BuildSystemPrompt(string? instructions)
    {
        var sb = new StringBuilder();
        sb.Append(
            "You are a semantic-diff engine inside an automation workflow. Compare the PREVIOUS and " +
            "CURRENT versions of a document by MEANING, not by characters.\n\n");
        sb.Append("Rules:\n");
        sb.Append("- A MATERIAL change alters meaning: facts, numbers, dates, deadlines, prices, obligations, scope, names, or terms.\n");
        sb.Append("- Formatting, whitespace, reordering, and synonym/wording rewrites that keep the same meaning are NOT material — list them as ignoredChanges.\n");
        sb.Append("- For each material change give a short snake_case type (e.g. deadline_changed, price_changed, obligation_added), the old and new value, and an impact of high, medium, or low.\n\n");

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            sb.Append("Additional guidance: ").Append(instructions).Append("\n\n");
        }

        sb.Append(
            "Respond with ONLY this JSON object (no markdown fences, no commentary):\n" +
            "{ \"materialChanges\": [ { \"type\": \"<slug>\", \"old\": \"<old>\", \"new\": \"<new>\", \"impact\": \"high|medium|low\" } ], " +
            "\"ignoredChanges\": [ \"<short description>\" ] }");

        return sb.ToString();
    }

    private static string? Input(NodeExecutionContext context, string key)
        => context.Inputs.TryGetValue(key, out var value) ? value?.ToString() : null;
}
