// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Xunit;

namespace Knotarium.Tests.Nodes;

/// <summary>
/// Covers the Switch, Transform and Merge executors. These three node types were declared in the
/// built-in catalog — so they appeared in the palette, passed validation and published fine — while
/// having no executor at all, which made every run that reached one fail with "No task implementation
/// registered". <see cref="BuiltInCatalogExecutorCoverageTests"/> is the guard against that recurring;
/// these are the behavioural tests.
/// </summary>
public class SwitchTransformMergeNodeTaskTests
{
    private static NodeExecutionContext Context(Dictionary<string, object> inputs) => new(
        WorkflowId: WorkflowDefinitionId.New(),
        ExecutionId: Guid.NewGuid(),
        NodeId: NodeId.Create("n1"),
        Inputs: inputs,
        GlobalVariables: new Dictionary<string, object>());

    private static Dictionary<string, object> Outputs(LegacyNodeResult result)
    {
        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        Assert.NotNull(success.Outputs);
        return success.Outputs!;
    }

    // ─────────────────────────────────────────────────────────────────────── Switch

    [Theory]
    [InlineData("paid", "paid")]
    [InlineData("PAID", "paid")]          // matching is case-insensitive…
    [InlineData("refunded", "refunded")]
    [InlineData("cancelled", "default")]  // …and an unmatched value falls through
    [InlineData("", "default")]
    public async Task Switch_selects_the_matching_case_or_default(string value, string expectedPort)
    {
        var result = await new SwitchNodeTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["value"] = value,
            ["cases"] = "paid, refunded, pending",
        }), CancellationToken.None);

        Assert.Equal(expectedPort, Outputs(result)["selectedPort"]);
    }

    [Fact]
    public async Task Switch_reports_the_matched_case_using_the_configured_spelling()
    {
        // The port name has to be the label as configured, not as supplied: edges are drawn against the
        // configured spelling, so echoing the caller's casing back would route nowhere.
        var result = await new SwitchNodeTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["value"] = "PAID",
            ["cases"] = "Paid, Refunded",
        }), CancellationToken.None);

        Assert.Equal("Paid", Outputs(result)["selectedPort"]);
    }

    [Theory]
    [InlineData("a,b,c")]
    [InlineData("a; b; c")]
    [InlineData("a\nb\nc")]
    [InlineData(" a , b ,, c ")]
    public void Switch_parses_every_supported_separator_and_trims(string raw)
    {
        Assert.Equal(new[] { "a", "b", "c" }, SwitchNodeTask.ParseCases(raw));
    }

    [Fact]
    public void Switch_dedupes_cases_case_insensitively_keeping_the_first_spelling()
    {
        // Two labels differing only in case would render two handles of which only the first could ever
        // be selected — so they collapse to one, and the canvas must agree (switchPorts.ts).
        Assert.Equal(new[] { "Paid" }, SwitchNodeTask.ParseCases("Paid, paid, PAID"));
    }

    [Fact]
    public void Switch_treats_missing_cases_as_no_branches()
    {
        Assert.Empty(SwitchNodeTask.ParseCases(null));
        Assert.Empty(SwitchNodeTask.ParseCases("   "));
    }

    // ──────────────────────────────────────────────────────────────────── Transform

    [Fact]
    public async Task Transform_extracts_a_nested_path()
    {
        var result = await new TransformNodeTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["inputJson"] = """{"data":{"user":{"id":42}}}""",
            ["jsonPath"] = "data.user.id",
        }), CancellationToken.None);

        var value = Assert.IsType<JsonElement>(Outputs(result)["success"]);
        Assert.Equal(42, value.GetInt32());
    }

    [Fact]
    public async Task Transform_publishes_under_the_declared_output_name()
    {
        // An edge resolves its payload by looking its OWN output name up in this dictionary, so the key
        // must match the manifest's declared "success" output or the wire is silently empty.
        var result = await new TransformNodeTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["inputJson"] = """{"a":1}""",
            ["jsonPath"] = "a",
        }), CancellationToken.None);

        Assert.Contains("success", Outputs(result).Keys);
    }

    [Fact]
    public async Task Transform_accepts_an_already_parsed_element()
    {
        // Upstream ports deliver a JsonElement; expression-substituted fields deliver a string. Both
        // have to behave identically or the result would depend on how the input happened to be wired.
        using var doc = JsonDocument.Parse("""{"items":[{"sku":"X1"}]}""");
        var result = await new TransformNodeTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["inputJson"] = doc.RootElement.Clone(),
            ["jsonPath"] = "items[0].sku",
        }), CancellationToken.None);

        Assert.Equal("X1", Assert.IsType<JsonElement>(Outputs(result)["success"]).GetString());
    }

    [Theory]
    [InlineData(null, "a", "'inputJson' is required")]
    [InlineData("""{"a":1}""", "", "'jsonPath' is required")]
    [InlineData("""{"a":1}""", "missing.branch", "did not match anything")]
    public async Task Transform_fails_loudly_rather_than_passing_nothing_downstream(string? json, string path, string expected)
    {
        var inputs = new Dictionary<string, object> { ["jsonPath"] = path };
        if (json is not null) inputs["inputJson"] = json;

        var result = await new TransformNodeTask().ExecuteAsync(Context(inputs), CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains(expected, failure.ErrorMessage);
    }

    // ─────────────────────────────────────────────────────────────────────── Merge

    [Fact]
    public async Task Merge_concatenates_both_arrays_in_order()
    {
        var result = await new MergeNodeTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["array1"] = "[1,2]",
            ["array2"] = "[3]",
        }), CancellationToken.None);

        var merged = Assert.IsType<JsonElement>(Outputs(result)["success"]);
        Assert.Equal(new[] { 1, 2, 3 }, merged.EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(3, Outputs(result)["count"]);
    }

    [Fact]
    public async Task Merge_passes_through_when_only_one_side_is_wired()
    {
        var result = await new MergeNodeTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["array1"] = "[\"a\"]",
        }), CancellationToken.None);

        Assert.Single(Assert.IsType<JsonElement>(Outputs(result)["success"]).EnumerateArray());
    }

    [Fact]
    public async Task Merge_appends_a_non_array_as_a_single_element()
    {
        // Merging one record onto a list is a normal intent; rejecting it would force a wrapper node.
        var result = await new MergeNodeTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["array1"] = "[1]",
            ["array2"] = """{"id":9}""",
        }), CancellationToken.None);

        var merged = Assert.IsType<JsonElement>(Outputs(result)["success"]);
        Assert.Equal(2, merged.GetArrayLength());
        Assert.Equal(9, merged[1].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Merge_of_nothing_is_an_empty_array_not_a_failure()
    {
        var result = await new MergeNodeTask().ExecuteAsync(Context(new Dictionary<string, object>()), CancellationToken.None);

        Assert.Equal(0, Assert.IsType<JsonElement>(Outputs(result)["success"]).GetArrayLength());
        Assert.Equal(0, Outputs(result)["count"]);
    }
}
