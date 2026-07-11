using System;
using System.Text.Json;
using Knotarium.Api.Services;
using Knotarium.Core.Contracts;
using Xunit;

namespace Knotarium.Tests.Reactive;

/// <summary>
/// The trigger-level field filter: an Event/Action Trigger reacts only when a chosen payload key compares
/// true. Covers the type-aware compare (numeric vs string), presence operators, fail-closed on a missing
/// field, and resolution of envelope conveniences (camera) alongside payload fields.
/// </summary>
public class InboundFieldPredicateTests
{
    private static JsonElement Json(string s) => JsonSerializer.Deserialize<JsonElement>(s);

    private static InboundEnvelope Env(string payloadJson, long? camera = null) =>
        new("sys", "siteA", "host", ExternalSignalKind.Action, "VCACountingAICar",
            GlobalCameraNumber: camera, ChannelId: null, Active: null, CorrelationKey: null,
            Payload: Json(payloadJson), Timestamp: DateTimeOffset.UnixEpoch);

    private static bool Match(string field, string op, string? value, string payload, long? camera = null) =>
        InboundFieldPredicate.Matches(new InboundFieldPredicate(field, op, value), Env(payload, camera));

    [Theory]
    [InlineData("equals", "Left", true)]
    [InlineData("equals", "left", true)]        // string equals is case-insensitive
    [InlineData("equals", "Right", false)]
    [InlineData("notEquals", "Right", true)]
    [InlineData("contains", "ef", true)]
    [InlineData("notContains", "zz", true)]
    public void String_compares_are_case_insensitive(string op, string value, bool expected)
    {
        Assert.Equal(expected, Match("Direction", op, value, """{ "Direction": "Left" }"""));
    }

    [Theory]
    [InlineData("equals", "5", true)]
    [InlineData("equals", "5.0", true)]         // numeric equality, not string
    [InlineData("greaterThan", "3", true)]
    [InlineData("greaterThan", "9", false)]
    [InlineData("lessThan", "9", true)]
    public void Numeric_compares_when_both_sides_parse_as_numbers(string op, string value, bool expected)
    {
        Assert.Equal(expected, Match("TotalCount", op, value, """{ "TotalCount": "5" }"""));
    }

    [Fact]
    public void Exists_and_notExists_check_only_presence()
    {
        Assert.True(Match("Make", "exists", null, """{ "Make": "VW" }"""));
        Assert.False(Match("Make", "exists", null, """{ "Color": "Red" }"""));
        Assert.True(Match("Make", "notExists", null, """{ "Color": "Red" }"""));
    }

    [Fact]
    public void A_missing_field_fails_closed_for_value_compares()
    {
        Assert.False(Match("Direction", "equals", "Left", """{ "Color": "Red" }"""));
        Assert.False(Match("Direction", "notEquals", "Left", """{ "Color": "Red" }"""));
    }

    [Fact]
    public void Unknown_operator_fails_closed()
    {
        Assert.False(Match("Direction", "startsWith", "Le", """{ "Direction": "Left" }"""));
    }

    [Fact]
    public void Envelope_conveniences_are_filterable_alongside_payload_fields()
    {
        Assert.True(Match("camera", "equals", "7", """{ "Direction": "Left" }""", camera: 7));
        Assert.True(Match("type", "equals", "VCACountingAICar", """{ }"""));
    }
}
