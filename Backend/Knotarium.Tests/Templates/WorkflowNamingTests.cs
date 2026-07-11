using System;
using Knotarium.Features.Portability;
using Xunit;

namespace Knotarium.Tests.Templates;

public class WorkflowNamingTests
{
    [Fact]
    public void Returns_the_name_unchanged_when_free()
        => Assert.Equal("Flow", WorkflowNaming.EnsureUnique("Flow", new[] { "Other" }));

    [Fact]
    public void Appends_2_on_first_collision()
        => Assert.Equal("Flow (2)", WorkflowNaming.EnsureUnique("Flow", new[] { "Flow" }));

    [Fact]
    public void Picks_the_lowest_free_suffix_across_gaps()
        // "Flow" and "Flow (4)" are taken; the lowest free is (2), not max+1 (5).
        => Assert.Equal("Flow (2)", WorkflowNaming.EnsureUnique("Flow", new[] { "Flow", "Flow (4)" }));

    [Fact]
    public void Fills_the_first_gap_in_the_sequence()
        => Assert.Equal("Flow (3)", WorkflowNaming.EnsureUnique("Flow", new[] { "Flow", "Flow (2)", "Flow (4)" }));

    [Fact]
    public void Treats_a_name_already_ending_in_a_suffix_literally()
        // The user typed "Flow (2)" and it exists → nest, don't reinterpret.
        => Assert.Equal("Flow (2) (2)", WorkflowNaming.EnsureUnique("Flow (2)", new[] { "Flow (2)" }));

    [Fact]
    public void Collisions_are_case_insensitive()
        // Detection ignores case; the returned name keeps the caller's casing.
        => Assert.Equal("flow (2)", WorkflowNaming.EnsureUnique("flow", new[] { "FLOW" }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Falls_back_to_Workflow_when_blank(string blank)
        => Assert.Equal("Workflow", WorkflowNaming.EnsureUnique(blank, Array.Empty<string>()));
}
