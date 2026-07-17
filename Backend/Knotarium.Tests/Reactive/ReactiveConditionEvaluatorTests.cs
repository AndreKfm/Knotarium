// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;
using Knotarium.Core.Contracts;
using Knotarium.Core.Reactive;
using Knotarium.Features.Reactive;
using Xunit;

namespace Knotarium.Tests.Reactive;

public class ReactiveConditionEvaluatorTests
{
    private static JsonElement Json(string s) => JsonSerializer.Deserialize<JsonElement>(s);

    private static ReactiveGuard Guard(string logicJson, bool expectTrue = true) =>
        new("C", expectTrue, Json(logicJson));

    private static InboundEnvelope Env(string payloadJson, long? camera = null) =>
        new("sys", "siteA", "host", ExternalSignalKind.Event, "Plate",
            GlobalCameraNumber: camera, ChannelId: null, Active: true, CorrelationKey: null,
            Payload: Json(payloadJson), Timestamp: DateTimeOffset.UnixEpoch);

    private const string PlateEqAbc =
        """{ "version":2, "root": { "kind":"cmp","id":"c","op":"eq", "a": { "kind":"ref","type":"string","ref": { "__type":"variable_ref","variableName":"plate" } }, "b": { "kind":"lit","type":"string","value":"ABC" } } }""";

    private const string PlateInWhitelist =
        """{ "version":2, "root": { "kind":"cmp","id":"c","op":"in", "a": { "kind":"ref","type":"string","ref": { "__type":"variable_ref","variableName":"plate" } }, "b": { "kind":"lit","type":"string","value":"ABC, DEF, GHI" } } }""";

    [Fact]
    public void True_branch_guard_passes_when_the_payload_field_matches()
    {
        Assert.True(ReactiveConditionEvaluator.Passes(Guard(PlateEqAbc), Env("""{ "plate": "ABC" }""")));
    }

    [Fact]
    public void True_branch_guard_fails_when_the_payload_field_differs()
    {
        Assert.False(ReactiveConditionEvaluator.Passes(Guard(PlateEqAbc), Env("""{ "plate": "ZZZ" }""")));
    }

    [Fact]
    public void False_branch_guard_passes_exactly_when_the_condition_is_false()
    {
        Assert.True(ReactiveConditionEvaluator.Passes(Guard(PlateEqAbc, expectTrue: false), Env("""{ "plate": "ZZZ" }""")));
        Assert.False(ReactiveConditionEvaluator.Passes(Guard(PlateEqAbc, expectTrue: false), Env("""{ "plate": "ABC" }""")));
    }

    [Fact]
    public void Membership_whitelist_matches_a_payload_value()
    {
        Assert.True(ReactiveConditionEvaluator.Passes(Guard(PlateInWhitelist), Env("""{ "plate": "DEF" }""")));
        Assert.False(ReactiveConditionEvaluator.Passes(Guard(PlateInWhitelist), Env("""{ "plate": "NOPE" }""")));
    }

    [Fact]
    public void An_unresolved_reference_fails_closed_for_both_senses()
    {
        var present = Env("""{ "other": "x" }"""); // no "plate"
        Assert.False(ReactiveConditionEvaluator.Passes(Guard(PlateEqAbc, expectTrue: true), present));
        Assert.False(ReactiveConditionEvaluator.Passes(Guard(PlateEqAbc, expectTrue: false), present));
    }

    [Fact]
    public void Envelope_level_camera_number_is_resolvable()
    {
        var logic =
            """{ "version":2, "root": { "kind":"cmp","id":"c","op":"eq", "a": { "kind":"ref","type":"number","ref":"camera" }, "b": { "kind":"lit","type":"number","value":101 } } }""";
        Assert.True(ReactiveConditionEvaluator.Passes(Guard(logic), Env("{}", camera: 101)));
        Assert.False(ReactiveConditionEvaluator.Passes(Guard(logic), Env("{}", camera: 7)));
    }

    [Fact]
    public void Unparseable_logic_fails_closed()
    {
        Assert.False(ReactiveConditionEvaluator.Passes(Guard("""{ "garbage": true }"""), Env("{}")));
    }

    [Fact]
    public void AllPass_is_true_for_an_empty_guard_chain()
    {
        Assert.True(ReactiveConditionEvaluator.AllPass(Array.Empty<ReactiveGuard>(), Env("{}")));
    }

    [Fact]
    public void AllPass_ands_the_chain()
    {
        var pass = Guard(PlateEqAbc);
        var fail = Guard(PlateInWhitelist); // "ABC" is in the whitelist, so this also passes
        var env = Env("""{ "plate": "ABC" }""");
        Assert.True(ReactiveConditionEvaluator.AllPass(new[] { pass, fail }, env));

        var blocked = Guard(PlateEqAbc, expectTrue: false); // requires plate != ABC
        Assert.False(ReactiveConditionEvaluator.AllPass(new[] { pass, blocked }, env));
    }
}
