// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Knotarium.Features.Condition;
using Knotarium.Features.Nodes.Condition;
using Xunit;

namespace Knotarium.Tests.Nodes.Condition;

/// <summary>
/// Tree-aggregation conformance (Phase 8, spec §10). Every case in condition-tree.fixture.json — the
/// SAME file the FE suite loads — is built into a <see cref="ResolvedLogicNode"/> tree and run through
/// <see cref="ConditionEvaluator.EvaluateTree"/>. Fixture leaves carry a precomputed status (not
/// operands), so this pins the fold (B8 NOT, B9 dominance, depth-first error surfacing) FE⇄BE, while
/// leaf operator semantics stay in condition-conformance.fixture.json.
/// </summary>
public class ConditionTreeEvaluatorTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var doc = ConditionFixtures.Load(ConditionFixtures.Tree);
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return new object[] { c.GetProperty("id").GetString()! };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Tree_matches_expected(string id)
    {
        using var doc = ConditionFixtures.Load(ConditionFixtures.Tree);
        var c = doc.RootElement.GetProperty("cases").EnumerateArray()
            .First(x => x.GetProperty("id").GetString() == id);

        var tree = BuildNode(c.GetProperty("tree"));
        var outcome = ConditionEvaluator.EvaluateTree(tree);

        var expected = Enum.Parse<ConditionStatus>(c.GetProperty("expect").GetString()!);
        Assert.Equal(expected, outcome.Status);

        if (c.TryGetProperty("expectErrorFrom", out var fromEl))
        {
            Assert.NotNull(outcome.Error);
            Assert.Equal(fromEl.GetString(), outcome.Error!.ComparatorId);
        }
    }

    // Build a resolved tree from a fixture node. A leaf maps its precomputed status to a deterministic
    // comparator (so EvaluateComparator yields exactly that status); the Error leaf carries its id so
    // the surfaced error's ComparatorId can be asserted (depth-first selection).
    private static ResolvedLogicNode BuildNode(JsonElement node)
    {
        return node.GetProperty("kind").GetString() switch
        {
            "leaf" => new ResolvedComparatorNode(LeafFor(
                Enum.Parse<ConditionStatus>(node.GetProperty("status").GetString()!),
                node.TryGetProperty("id", out var idEl) ? idEl.GetString()! : "leaf")),
            "group" => new ResolvedGroupNode(
                Enum.Parse<Combinator>(Capitalize(node.GetProperty("op").GetString()!)),
                node.GetProperty("children").EnumerateArray().Select(BuildNode).ToList()),
            "not" => new ResolvedNotNode(BuildNode(node.GetProperty("child"))),
            var k => throw new InvalidOperationException($"unknown fixture node kind '{k}'"),
        };
    }

    private static ResolvedComparator LeafFor(ConditionStatus status, string id)
    {
        ResolvedOperand Num(long v) => ResolvedOperand.Value(OperandType.Number, v);
        return status switch
        {
            ConditionStatus.True => new ResolvedComparator(id, "eq", Num(1), Num(1)),
            ConditionStatus.False => new ResolvedComparator(id, "eq", Num(1), Num(2)),
            // §2.2 unset → Incomplete.
            ConditionStatus.Incomplete => new ResolvedComparator(id, "eq", ResolvedOperand.Unset(OperandType.Number), Num(1)),
            // §2.3 unresolved → RESOLUTION_FAILED, error.ComparatorId == id.
            ConditionStatus.Error => new ResolvedComparator(id, "eq", ResolvedOperand.Unresolved(OperandType.Number), Num(1)),
            _ => throw new InvalidOperationException($"unhandled status {status}"),
        };
    }

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
