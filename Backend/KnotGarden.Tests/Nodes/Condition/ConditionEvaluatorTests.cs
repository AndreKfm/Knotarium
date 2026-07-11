using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KnotGarden.Features.Condition;
using KnotGarden.Features.Nodes.Condition;
using Xunit;

namespace KnotGarden.Tests.Nodes.Condition;

/// <summary>
/// Fixture-driven conformance tests (B2). Every case in condition-conformance.fixture.json is run
/// through the pure <see cref="ConditionEvaluator"/> and checked against its expected status/code —
/// the enforcement that the backend honors docs/design/condition-operator-semantics.md, and (via the
/// FE running the same fixture) that "what you see == what runs".
/// </summary>
public class ConditionEvaluatorTests
{
    // ── Single-comparator cases ──

    public static IEnumerable<object[]> Cases()
    {
        using var doc = ConditionFixtures.Load(ConditionFixtures.Conformance);
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return new object[] { c.GetProperty("id").GetString()! };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Comparator_matches_expected(string id)
    {
        var c = FindCase("cases", id);

        var op = c.GetProperty("op").GetString()!;
        var a = ParseOperand(c.GetProperty("a"));
        ResolvedOperand? b = c.TryGetProperty("b", out var bEl) ? ParseOperand(bEl) : null;

        var result = ConditionEvaluator.EvaluateComparator(new ResolvedComparator(id, op, a, b));

        var expect = c.GetProperty("expect");
        var expectedStatus = ParseStatus(expect.GetProperty("status").GetString()!);
        Assert.Equal(expectedStatus, result.Status);

        if (expectedStatus == ConditionStatus.Error)
        {
            Assert.NotNull(result.Error);
            var expectedCode = Enum.Parse<ConditionErrorCode>(expect.GetProperty("code").GetString()!);
            Assert.Equal(expectedCode, result.Error!.Code);

            // operand is asserted when the fixture pins it (present, may be JSON null).
            if (expect.TryGetProperty("operand", out var opEl))
            {
                string? expectedOperand = opEl.ValueKind == JsonValueKind.Null ? null : opEl.GetString();
                Assert.Equal(expectedOperand, result.Error.Operand);
            }
            Assert.Equal(id, result.Error.ComparatorId);
        }
        else
        {
            Assert.Null(result.Error);
        }
    }

    // ── Aggregation cases ──

    public static IEnumerable<object[]> AggregationCases()
    {
        using var doc = ConditionFixtures.Load(ConditionFixtures.Conformance);
        foreach (var c in doc.RootElement.GetProperty("aggregation").EnumerateArray())
        {
            yield return new object[] { c.GetProperty("id").GetString()! };
        }
    }

    [Theory]
    [MemberData(nameof(AggregationCases))]
    public void Aggregation_matches_expected(string id)
    {
        var c = FindCase("aggregation", id);

        var comb = c.GetProperty("comb").GetString() == "and" ? Combinator.And : Combinator.Or;
        var results = c.GetProperty("statuses").EnumerateArray()
            .Select((s, i) => MakeResult($"c{i}", ParseStatus(s.GetString()!)))
            .ToList();

        var outcome = ConditionEvaluator.Aggregate(comb, results);

        Assert.Equal(ParseStatus(c.GetProperty("expect").GetString()!), outcome.Status);
    }

    [Fact]
    public void Aggregate_reports_first_error_by_order()
    {
        var results = new[]
        {
            MakeResult("c0", ConditionStatus.True),
            MakeResult("c1", ConditionStatus.Error),
            MakeResult("c2", ConditionStatus.Error),
        };

        var outcome = ConditionEvaluator.Aggregate(Combinator.And, results);

        Assert.Equal(ConditionStatus.Error, outcome.Status);
        Assert.Equal("c1", outcome.Error!.ComparatorId); // first error, not the last
    }

    // ── Helpers ──

    private static JsonElement FindCase(string section, string id)
    {
        using var doc = ConditionFixtures.Load(ConditionFixtures.Conformance);
        foreach (var c in doc.RootElement.GetProperty(section).EnumerateArray())
        {
            if (c.GetProperty("id").GetString() == id)
            {
                return c.Clone(); // clone so it survives the using-disposed document
            }
        }
        throw new InvalidOperationException($"Case '{id}' not found in fixture section '{section}'.");
    }

    private static ResolvedOperand ParseOperand(JsonElement el)
    {
        var type = el.GetProperty("type").GetString() switch
        {
            "string" => OperandType.String,
            "number" => OperandType.Number,
            "boolean" => OperandType.Boolean,
            var other => throw new InvalidOperationException($"Unknown operand type '{other}'."),
        };

        var state = el.TryGetProperty("state", out var stateEl) ? stateEl.GetString() : "value";
        return state switch
        {
            "unset" => ResolvedOperand.Unset(type),
            "unresolved" => ResolvedOperand.Unresolved(type),
            "absent" => ResolvedOperand.Absent(type),
            // "value" (default): pass the raw JsonElement straight through — the evaluator normalizes
            // it, so this also exercises the JsonElement path. raw may be JSON null (a legitimate null).
            _ => ResolvedOperand.Value(type, el.TryGetProperty("raw", out var raw) ? (object?)raw : null),
        };
    }

    private static ConditionStatus ParseStatus(string s) => Enum.Parse<ConditionStatus>(s);

    private static ComparatorResult MakeResult(string id, ConditionStatus status) => status switch
    {
        ConditionStatus.True => ComparatorResult.Ok(id, true),
        ConditionStatus.False => ComparatorResult.Ok(id, false),
        ConditionStatus.Incomplete => ComparatorResult.Incomplete(id),
        ConditionStatus.Error => ComparatorResult.Fail(id,
            new ConditionError(ConditionErrorCode.INVALID_LOGIC, "synthetic", id, null)),
        _ => throw new InvalidOperationException($"Unhandled status '{status}'."),
    };
}
