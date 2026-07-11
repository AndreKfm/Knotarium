using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KnotGarden.Features.Condition;
using KnotGarden.Features.Nodes.Condition;
using Xunit;

namespace KnotGarden.Tests.Nodes.Condition;

/// <summary>
/// Catalog drift test (B2): the backend <see cref="ConditionOperatorCatalog"/> must match
/// condition-catalog.fixture.json exactly (id, group, arity, accepts, order). The FE has the mirror
/// test, so neither language can silently diverge from the shared catalog.
/// </summary>
public class ConditionOperatorCatalogTests
{
    private static List<JsonElement> FixtureOperators()
    {
        using var doc = ConditionFixtures.Load(ConditionFixtures.Catalog);
        return doc.RootElement.GetProperty("operators").EnumerateArray()
            .Select(e => e.Clone())
            .ToList();
    }

    [Fact]
    public void Catalog_has_same_operator_ids_in_the_same_order()
    {
        var fixtureIds = FixtureOperators().Select(o => o.GetProperty("id").GetString()!).ToList();
        var codeIds = ConditionOperatorCatalog.Operators.Select(o => o.Id).ToList();

        Assert.Equal(fixtureIds, codeIds);
    }

    [Fact]
    public void Catalog_entries_match_group_arity_and_accepts()
    {
        var fixture = FixtureOperators();
        foreach (var fx in fixture)
        {
            var id = fx.GetProperty("id").GetString()!;
            Assert.True(ConditionOperatorCatalog.TryGet(id, out var def), $"Operator '{id}' missing from backend catalog.");

            Assert.Equal(fx.GetProperty("group").GetString(), def.Group);

            var expectedArity = fx.GetProperty("arity").GetString() == "unary"
                ? OperatorArity.Unary
                : OperatorArity.Binary;
            Assert.Equal(expectedArity, def.Arity);

            var expectedAccepts = fx.GetProperty("accepts").EnumerateArray().Select(a => a.GetString()!).ToList();
            Assert.Equal(expectedAccepts, def.Accepts.ToList());
        }
    }

    [Fact]
    public void Catalog_has_no_extra_operators_beyond_the_fixture()
    {
        var fixtureIds = FixtureOperators().Select(o => o.GetProperty("id").GetString()).ToHashSet();
        var extras = ConditionOperatorCatalog.Operators.Select(o => o.Id).Where(id => !fixtureIds.Contains(id)).ToList();

        Assert.True(extras.Count == 0, $"Backend catalog has operators not in the fixture: {string.Join(", ", extras)}");
    }
}
