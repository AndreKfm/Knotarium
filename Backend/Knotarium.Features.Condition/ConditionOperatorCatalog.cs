using System;
using System.Collections.Generic;
using System.Linq;

namespace Knotarium.Features.Condition;

/// <summary>Operator arity (spec §1 / catalog fixture).</summary>
public enum OperatorArity
{
    Unary,
    Binary
}

/// <summary>One operator catalog entry — the shared FE/BE source of truth (id, group, arity, accepts).</summary>
public sealed record OperatorDefinition(
    string Id,
    string Group,
    OperatorArity Arity,
    IReadOnlyList<string> Accepts);

/// <summary>
/// The backend operator catalog. This must stay byte-for-byte equivalent (per id/group/arity/accepts)
/// to <c>docs/design/condition-catalog.fixture.json</c> — a drift test asserts it, mirroring the
/// FE drift test, so "what you see == what runs" holds across both languages (B2).
/// </summary>
public static class ConditionOperatorCatalog
{
    public static readonly IReadOnlyList<OperatorDefinition> Operators = new List<OperatorDefinition>
    {
        new("eq",        "Comparison", OperatorArity.Binary, new[] { "string", "number", "boolean", "any" }),
        new("ne",        "Comparison", OperatorArity.Binary, new[] { "string", "number", "boolean", "any" }),
        new("gt",        "Comparison", OperatorArity.Binary, new[] { "string", "number", "any" }),
        new("gte",       "Comparison", OperatorArity.Binary, new[] { "string", "number", "any" }),
        new("lt",        "Comparison", OperatorArity.Binary, new[] { "string", "number", "any" }),
        new("lte",       "Comparison", OperatorArity.Binary, new[] { "string", "number", "any" }),

        new("contains",  "Text", OperatorArity.Binary, new[] { "string", "array", "any" }),
        new("ncontains", "Text", OperatorArity.Binary, new[] { "string", "array", "any" }),
        new("starts",    "Text", OperatorArity.Binary, new[] { "string", "any" }),
        new("ends",      "Text", OperatorArity.Binary, new[] { "string", "any" }),
        new("regex",     "Text", OperatorArity.Binary, new[] { "string", "any" }),

        new("in",  "Membership", OperatorArity.Binary, new[] { "string", "number", "any" }),
        new("nin", "Membership", OperatorArity.Binary, new[] { "string", "number", "any" }),

        new("empty",   "Existence", OperatorArity.Unary, new[] { "string", "array", "object", "any" }),
        new("nempty",  "Existence", OperatorArity.Unary, new[] { "string", "array", "object", "any" }),
        new("exists",  "Existence", OperatorArity.Unary, new[] { "any" }),
        new("nexists", "Existence", OperatorArity.Unary, new[] { "any" }),

        new("true",  "Boolean", OperatorArity.Unary, new[] { "boolean", "any" }),
        new("false", "Boolean", OperatorArity.Unary, new[] { "boolean", "any" }),
    };

    private static readonly Dictionary<string, OperatorDefinition> ById =
        Operators.ToDictionary(o => o.Id, StringComparer.Ordinal);

    public static bool TryGet(string id, out OperatorDefinition definition) =>
        ById.TryGetValue(id, out definition!);

    public static bool IsKnown(string id) => ById.ContainsKey(id);
}
