// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Portability;

// ─────────────────────────────────────────────────────────────────────────────
// Credential-slot module — the single home for the recursive node-property walk
// plus BOTH portability directions, shared by bundles and templates so the two
// can never drift:
//
//   ExtractIdsToSlots  (export): host-specific credential ids  →  slot:<key>
//   RebindSlotsToIds   (install): slot:<key>  →  host credential ids
//
// Pure and DB-free: callers supply the id↔name / slot↔id maps; this only rewrites
// the document. Node property values arrive either as System.Text.Json JsonElement
// (the deserialized import path) or raw CLR values (in-memory), and tokens can sit
// at any depth, so the walk handles both representations and recurses through
// objects/arrays.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The placeholder prefixes a portable workflow uses for credential slots and parameters.</summary>
public static class CredentialSlotTokens
{
    public const string SlotPrefix = "slot:";

    /// <summary>The slot-key grammar: a lowercase-kebab token, 1–64 chars, leading letter.</summary>
    public const string SlotKeyPattern = "^[a-z][a-z0-9-]{0,63}$";

    /// <summary>The opening/closing of a parameter token, e.g. <c>{{param:slack_channel}}</c>.</summary>
    public const string ParamPrefix = "{{param:";
    public const string ParamSuffix = "}}";
}

/// <summary>
/// A coerced, ready-to-substitute parameter value: the typed scalar used when a property's <em>entire</em>
/// value is a single <c>{{param:key}}</c> token (so a number/bool stays JSON-typed), plus the string form
/// used when a token is embedded inside a larger string. Produced by the Templates-layer validator so that
/// <see cref="CredentialSlotModule.SubstituteParameters"/> is total and cannot itself fail.
/// </summary>
public sealed record ParameterValue(object? Boxed, string Text);

/// <summary>A credential reference lifted out of a workflow during export.</summary>
/// <param name="Slot">The stable, validated slot key (matches <see cref="CredentialSlotTokens.SlotKeyPattern"/>).</param>
/// <param name="DisplayName">The original credential name, verbatim, for the binding UI.</param>
/// <param name="SourceCredentialId">The host credential id this slot was extracted from.</param>
public sealed record PortableCredentialSlot(string Slot, string DisplayName, string SourceCredentialId);

/// <summary>Result of <see cref="CredentialSlotModule.ExtractIdsToSlots"/>.</summary>
public sealed record CredentialSlotExtractResult(
    WorkflowExportDocument Document,
    IReadOnlyList<PortableCredentialSlot> Slots,
    IReadOnlyList<string> RewrittenPaths);

/// <summary>Result of <see cref="CredentialSlotModule.RebindSlotsToIds"/>.</summary>
public sealed record CredentialSlotRebindResult(
    WorkflowExportDocument Document,
    IReadOnlyList<string> ReboundSlots,
    IReadOnlyList<string> UnboundSlots);

/// <summary>Pure, bidirectional rewrite of credential references in a workflow's node properties.</summary>
public static class CredentialSlotModule
{
    /// <summary>
    /// Export direction. Replaces every node-property string whose entire value equals a known credential
    /// id (a key of <paramref name="credentialIdToName"/>) with a stable <c>slot:&lt;key&gt;</c> placeholder,
    /// and declares one slot per distinct id. The id→slot mapping is built once from the ids sorted ordinally,
    /// so it is deterministic and the same id always maps to the same slot within one export (N references →
    /// 1 slot). The content checksum is recomputed when anything changes.
    /// </summary>
    public static CredentialSlotExtractResult ExtractIdsToSlots(
        WorkflowExportDocument document,
        IReadOnlyDictionary<string, string> credentialIdToName)
    {
        ArgumentNullException.ThrowIfNull(document);
        credentialIdToName ??= new Dictionary<string, string>();

        // Pass 1: discover which credential ids are actually referenced (so we only declare used slots).
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in document.Content.Nodes)
        {
            foreach (var (_, value) in node.Properties)
            {
                CollectReferencedIds(value, credentialIdToName, referenced);
            }
        }

        // Assign stable slot keys in ordinal id order so the mapping is reproducible across exports.
        var idToSlot = new Dictionary<string, PortableCredentialSlot>(StringComparer.Ordinal);
        var usedSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in referenced.OrderBy(value => value, StringComparer.Ordinal))
        {
            var displayName = credentialIdToName[id];
            var slot = AllocateSlot(displayName, usedSlots);
            idToSlot[id] = new PortableCredentialSlot(slot, displayName, id);
        }

        var rewriter = new ExtractRewriter(idToSlot);
        var (newDocument, _) = ApplyWalk(document, rewriter);
        return new CredentialSlotExtractResult(
            newDocument,
            idToSlot.Values.OrderBy(slot => slot.Slot, StringComparer.Ordinal).ToList(),
            rewriter.RewrittenPaths);
    }

    /// <summary>
    /// Install direction. Rewrites every <c>slot:&lt;key&gt;</c> placeholder to the bound credential id from
    /// <paramref name="slotBindings"/>. Placeholders whose slot has no binding are left untouched and reported
    /// in <see cref="CredentialSlotRebindResult.UnboundSlots"/> — leaving a placeholder is safe because imported
    /// workflows are inactive and blocked from publish/run until every slot is bound. The checksum is recomputed
    /// when anything changes.
    /// </summary>
    public static CredentialSlotRebindResult RebindSlotsToIds(
        WorkflowExportDocument document,
        IReadOnlyDictionary<string, string> slotBindings)
    {
        ArgumentNullException.ThrowIfNull(document);
        slotBindings ??= new Dictionary<string, string>();

        var rewriter = new RebindRewriter(slotBindings);
        var (newDocument, _) = ApplyWalk(document, rewriter);
        return new CredentialSlotRebindResult(
            newDocument,
            rewriter.Rebound.ToList(),
            rewriter.Unbound.ToList());
    }

    /// <summary>Enumerates the slot keys still present as <c>slot:</c> placeholders in the document.</summary>
    public static IReadOnlyList<string> FindUnboundSlots(WorkflowExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return FindUnboundSlots(document.Content.Nodes);
    }

    /// <summary>Enumerates the slot keys still present as <c>slot:</c> placeholders across the given nodes.</summary>
    public static IReadOnlyList<string> FindUnboundSlots(IEnumerable<NodeDefinition> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var slots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            foreach (var (_, value) in node.Properties)
            {
                CollectSlotTokens(value, slots);
            }
        }

        return slots.OrderBy(slot => slot, StringComparer.Ordinal).ToList();
    }

    // ── Parameter substitution (third direction) ─────────────────────────────

    private static readonly Regex ParamTokenPattern =
        new(@"\{\{param:(?<key>[^}]+)\}\}", RegexOptions.Compiled);

    // Anchored: the WHOLE value is exactly one token (no leading/trailing chars, no second token).
    // "{{param:a}}{{param:b}}" and " {{param:x}}" deliberately fail this → handled as embedded.
    private static readonly Regex WholeParamTokenPattern =
        new(@"^\{\{param:(?<key>[^}]+)\}\}$", RegexOptions.Compiled);

    /// <summary>
    /// Substitution direction. Replaces <c>{{param:key}}</c> tokens in node properties with the supplied
    /// values. A property whose entire value is a single token becomes the <em>typed</em> scalar
    /// (<c>number</c>/<c>boolean</c> stay JSON-typed); a token embedded in a larger string is interpolated
    /// as text. Each leaf is visited once — a substituted value is never re-scanned, so a value that itself
    /// contains a token can't be re-interpreted in this pass. Tokens whose key is absent from
    /// <paramref name="values"/> are left intact (an authoring bug, surfaced by
    /// <see cref="FindUnsubstitutedParameters"/>). The checksum is recomputed when anything changes.
    /// </summary>
    public static WorkflowExportDocument SubstituteParameters(
        WorkflowExportDocument document,
        IReadOnlyDictionary<string, ParameterValue> values)
    {
        ArgumentNullException.ThrowIfNull(document);
        values ??= new Dictionary<string, ParameterValue>(StringComparer.Ordinal);

        var (newDocument, _) = ApplyTransform(document, (text, _) =>
        {
            var whole = WholeParamTokenPattern.Match(text);
            if (whole.Success && values.TryGetValue(whole.Groups["key"].Value, out var typed))
            {
                return (typed.Boxed, true);
            }

            if (ParamTokenPattern.IsMatch(text))
            {
                var rewritten = ParamTokenPattern.Replace(text, match =>
                    values.TryGetValue(match.Groups["key"].Value, out var embedded) ? embedded.Text : match.Value);
                return rewritten == text ? (text, false) : (rewritten, true);
            }

            return (text, false);
        });

        return newDocument;
    }

    /// <summary>
    /// True when <paramref name="text"/> contains a recognized <c>{{param:key}}</c> token — the exact same
    /// recognition the substitution pass uses. Validation reuses this (rather than a looser <c>Contains</c>)
    /// so a parameter value is only rejected for carrying a token the rewriter would actually act on.
    /// </summary>
    public static bool ContainsParameterToken(string text)
        => !string.IsNullOrEmpty(text) && ParamTokenPattern.IsMatch(text);

    /// <summary>True when the whole value is a credential-slot token (what <see cref="RebindSlotsToIds"/> rebinds).</summary>
    public static bool IsCredentialSlotToken(string text)
        => text is not null && text.StartsWith(CredentialSlotTokens.SlotPrefix, StringComparison.Ordinal);

    /// <summary>Enumerates the parameter keys still present as <c>{{param:key}}</c> tokens in the document.</summary>
    public static IReadOnlyList<string> FindUnsubstitutedParameters(WorkflowExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in document.Content.Nodes)
        {
            foreach (var (_, value) in node.Properties)
            {
                Walk(value, string.Empty, (text, _) =>
                {
                    foreach (Match match in ParamTokenPattern.Matches(text))
                    {
                        keys.Add(match.Groups["key"].Value);
                    }

                    return (text, false);
                });
            }
        }

        return keys.OrderBy(key => key, StringComparer.Ordinal).ToList();
    }

    // ── Slot-key generation ──────────────────────────────────────────────────

    /// <summary>
    /// Allocate a fresh, grammar-valid slot key (<see cref="CredentialSlotTokens.SlotKeyPattern"/>) from an
    /// arbitrary source string, avoiding collisions with keys already in <paramref name="usedKeys"/> (the set
    /// is updated with the chosen key). Exposes the same slug + collision rules used by export so other
    /// producers of slot tokens — e.g. AI workflow generation — can't drift from the canonical grammar.
    /// </summary>
    public static string AllocateSlotKey(string source, HashSet<string> usedKeys)
    {
        ArgumentNullException.ThrowIfNull(usedKeys);
        return AllocateSlot(source ?? string.Empty, usedKeys);
    }

    private static string AllocateSlot(string displayName, HashSet<string> used)
    {
        var baseSlot = Slugify(displayName);
        if (used.Add(baseSlot))
        {
            return baseSlot;
        }

        // Collision (incl. case-insensitive, e.g. "Production Camera" vs "production-camera"): suffix -2, -3 …
        for (var suffix = 2; ; suffix++)
        {
            var tail = "-" + suffix;
            var trimmed = baseSlot.Length + tail.Length > 64 ? baseSlot[..(64 - tail.Length)] : baseSlot;
            var candidate = trimmed + tail;
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string Slugify(string name)
    {
        var builder = new StringBuilder(name.Length);
        var lastWasDash = false;
        foreach (var ch in name.ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                builder.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');

        // The grammar requires a leading letter; prefix when empty or starting with a digit.
        if (slug.Length == 0)
        {
            return "credential";
        }

        if (slug[0] < 'a' || slug[0] > 'z')
        {
            slug = "c-" + slug;
        }

        return slug.Length > 64 ? slug[..64].TrimEnd('-') : slug;
    }

    // ── Recursive walk ───────────────────────────────────────────────────────

    private interface IStringRewriter
    {
        (object? Value, bool Changed) Rewrite(string text, string path);
    }

    /// <summary>
    /// A leaf callback: given a string leaf and its path, return its replacement and whether it changed.
    /// The string rewriters return a string; whole-value parameter substitution may return a typed
    /// (boxed) scalar here — the rebuilt property bag holds <c>object</c>, so either flows through.
    /// </summary>
    private delegate (object? Value, bool Changed) ScalarTransform(string text, string path);

    // Adapter: the credential rewriters are unchanged string→string transforms over the same walk.
    private static (WorkflowExportDocument Document, bool Changed) ApplyWalk(
        WorkflowExportDocument document,
        IStringRewriter rewriter)
        => ApplyTransform(document, rewriter.Rewrite);

    private static (WorkflowExportDocument Document, bool Changed) ApplyTransform(
        WorkflowExportDocument document,
        ScalarTransform transform)
    {
        var anyChange = false;
        var newNodes = new List<NodeDefinition>(document.Content.Nodes.Count);
        foreach (var node in document.Content.Nodes)
        {
            var newProperties = new Dictionary<string, object>(node.Properties.Count);
            var nodeChanged = false;
            foreach (var (key, value) in node.Properties)
            {
                var (transformed, changed) = Walk(value, $"{node.Id.Value}.{key}", transform);
                newProperties[key] = transformed!;
                nodeChanged |= changed;
            }

            newNodes.Add(nodeChanged ? node with { Properties = newProperties } : node);
            anyChange |= nodeChanged;
        }

        if (!anyChange)
        {
            return (document, false);
        }

        var content = new WorkflowExportContent(newNodes, document.Content.Edges);
        var manifest = document.Manifest with { Checksum = WorkflowVersionSerializer.ComputeChecksum(content) };
        return (new WorkflowExportDocument(manifest, content), true);
    }

    private static (object? Value, bool Changed) Walk(object? value, string path, ScalarTransform transform)
    {
        switch (value)
        {
            case string text:
                return transform(text, path);

            case JsonElement element:
                return WalkJson(element, path, transform);

            case IReadOnlyDictionary<string, object> dict:
            {
                var rebuilt = new Dictionary<string, object>(dict.Count);
                var changed = false;
                foreach (var (key, item) in dict)
                {
                    var (newItem, itemChanged) = Walk(item, $"{path}.{key}", transform);
                    rebuilt[key] = newItem!;
                    changed |= itemChanged;
                }

                return changed ? (rebuilt, true) : (value, false);
            }

            // Strings are IEnumerable too, but the `string` case above already claimed them.
            case System.Collections.IEnumerable sequence:
            {
                var rebuilt = new List<object?>();
                var changed = false;
                var index = 0;
                foreach (var item in sequence)
                {
                    var (newItem, itemChanged) = Walk(item, $"{path}[{index}]", transform);
                    rebuilt.Add(newItem);
                    changed |= itemChanged;
                    index++;
                }

                return changed ? (rebuilt, true) : (value, false);
            }

            default:
                return (value, false);
        }
    }

    private static (object? Value, bool Changed) WalkJson(JsonElement element, string path, ScalarTransform transform)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return transform(element.GetString() ?? string.Empty, path);

            case JsonValueKind.Object:
            {
                var rebuilt = new Dictionary<string, object>();
                var changed = false;
                foreach (var property in element.EnumerateObject())
                {
                    var (newItem, itemChanged) = WalkJson(property.Value, $"{path}.{property.Name}", transform);
                    rebuilt[property.Name] = newItem!;
                    changed |= itemChanged;
                }

                return changed ? (rebuilt, true) : (element, false);
            }

            case JsonValueKind.Array:
            {
                var rebuilt = new List<object?>();
                var changed = false;
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var (newItem, itemChanged) = WalkJson(item, $"{path}[{index}]", transform);
                    rebuilt.Add(newItem);
                    changed |= itemChanged;
                    index++;
                }

                return changed ? (rebuilt, true) : (element, false);
            }

            default:
                return (element, false);
        }
    }

    private static void CollectReferencedIds(
        object? value,
        IReadOnlyDictionary<string, string> credentialIdToName,
        HashSet<string> referenced)
    {
        Walk(value, string.Empty, new CollectIdsRewriter(credentialIdToName, referenced).Rewrite);
    }

    private static void CollectSlotTokens(object? value, HashSet<string> slots)
    {
        Walk(value, string.Empty, new CollectSlotsRewriter(slots).Rewrite);
    }

    // ── Rewriters ────────────────────────────────────────────────────────────

    private sealed class ExtractRewriter(IReadOnlyDictionary<string, PortableCredentialSlot> idToSlot) : IStringRewriter
    {
        private readonly List<string> _rewrittenPaths = [];

        public IReadOnlyList<string> RewrittenPaths => _rewrittenPaths;

        public (object? Value, bool Changed) Rewrite(string text, string path)
        {
            if (idToSlot.TryGetValue(text, out var slot))
            {
                _rewrittenPaths.Add(path);
                return (CredentialSlotTokens.SlotPrefix + slot.Slot, true);
            }

            return (text, false);
        }
    }

    private sealed class RebindRewriter(IReadOnlyDictionary<string, string> bindings) : IStringRewriter
    {
        public HashSet<string> Rebound { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Unbound { get; } = new(StringComparer.Ordinal);

        public (object? Value, bool Changed) Rewrite(string text, string path)
        {
            if (!text.StartsWith(CredentialSlotTokens.SlotPrefix, StringComparison.Ordinal))
            {
                return (text, false);
            }

            var slot = text[CredentialSlotTokens.SlotPrefix.Length..];
            if (bindings.TryGetValue(slot, out var credentialId))
            {
                Rebound.Add(slot);
                return (credentialId, true);
            }

            Unbound.Add(slot);
            return (text, false);
        }
    }

    private sealed class CollectIdsRewriter(
        IReadOnlyDictionary<string, string> credentialIdToName,
        HashSet<string> referenced) : IStringRewriter
    {
        public (object? Value, bool Changed) Rewrite(string text, string path)
        {
            if (credentialIdToName.ContainsKey(text))
            {
                referenced.Add(text);
            }

            return (text, false);
        }
    }

    private sealed class CollectSlotsRewriter(HashSet<string> slots) : IStringRewriter
    {
        public (object? Value, bool Changed) Rewrite(string text, string path)
        {
            if (text.StartsWith(CredentialSlotTokens.SlotPrefix, StringComparison.Ordinal))
            {
                slots.Add(text[CredentialSlotTokens.SlotPrefix.Length..]);
            }

            return (text, false);
        }
    }
}
