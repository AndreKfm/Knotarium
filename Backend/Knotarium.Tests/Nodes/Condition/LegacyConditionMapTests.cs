// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using Knotarium.Features.Nodes;
using Knotarium.Features.Condition;
using Knotarium.Features.Nodes.Condition;
using Xunit;

namespace Knotarium.Tests.Nodes.Condition;

/// <summary>
/// B6 contract: every shipped legacy <see cref="ConditionOperator"/> must map to a known OperatorId.
/// This is the drift guard that no legacy operator is left unmapped (which would route corrupt data).
/// </summary>
public class LegacyConditionMapTests
{
    [Fact]
    public void Every_legacy_operator_is_mapped_to_a_known_operator_id()
    {
        foreach (ConditionOperator op in Enum.GetValues<ConditionOperator>())
        {
            Assert.True(LegacyConditionMap.OperatorIds.TryGetValue(op, out var id),
                $"Legacy operator '{op}' is not mapped (B6 violation).");
            Assert.True(ConditionOperatorCatalog.IsKnown(id!),
                $"Legacy operator '{op}' maps to unknown OperatorId '{id}'.");
        }
    }

    [Fact]
    public void Not_equals_maps_to_ne_not_the_prototype_neq()
    {
        Assert.Equal("ne", LegacyConditionMap.OperatorIds[ConditionOperator.NotEqual]);
    }

    [Theory]
    [InlineData("Equal", "eq")]
    [InlineData("notequal", "ne")]
    [InlineData("GREATERTHAN", "gt")]
    [InlineData("Contains", "contains")]
    public void Maps_legacy_names_case_insensitively(string name, string expectedId)
    {
        Assert.True(LegacyConditionMap.TryMapOperatorName(name, out var id));
        Assert.Equal(expectedId, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("frobnicate")]
    [InlineData("neq")] // the prototype id never shipped — must NOT resolve
    public void Unknown_legacy_names_are_rejected(string? name)
    {
        Assert.False(LegacyConditionMap.TryMapOperatorName(name, out _));
    }
}
