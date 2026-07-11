using System;
using System.Collections.Generic;
using System.Text.Json;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Reactive;
using KnotGarden.Features.Reactive;
using Xunit;

namespace KnotGarden.Tests.Reactive;

public class ReactiveStepProcessorTests
{
    private static JsonElement Json(string s) => JsonSerializer.Deserialize<JsonElement>(s);

    private static InboundEnvelope Env(string payloadJson = "{}") =>
        new("sys", "siteA", "host", ExternalSignalKind.Event, "Plate",
            GlobalCameraNumber: null, ChannelId: null, Active: true, CorrelationKey: null,
            Payload: Json(payloadJson), Timestamp: DateTimeOffset.UnixEpoch);

    private static ReactiveGuard Guard(string logicJson, bool expectTrue = true) =>
        new("C", expectTrue, Json(logicJson));

    // flag == "on"
    private const string FlagOn =
        """{ "version":2, "root": { "kind":"cmp","id":"c","op":"eq", "a": { "kind":"ref","type":"string","ref":"flag" }, "b": { "kind":"lit","type":"string","value":"on" } } }""";

    // p in "ABC, DEF"
    private const string PInWhitelist =
        """{ "version":2, "root": { "kind":"cmp","id":"c","op":"in", "a": { "kind":"ref","type":"string","ref":"p" }, "b": { "kind":"lit","type":"string","value":"ABC, DEF" } } }""";

    [Fact]
    public void Empty_steps_pass()
    {
        Assert.True(ReactiveStepProcessor.Passes(Array.Empty<ReactiveStep>(), Env()));
    }

    [Fact]
    public void A_literal_transform_feeds_a_downstream_guard()
    {
        var steps = new ReactiveStep[]
        {
            new ReactiveTransform("S", new[] { new ReactiveAssignment("flag", "on") }),
            Guard(FlagOn),
        };
        Assert.True(ReactiveStepProcessor.Passes(steps, Env()));
    }

    [Fact]
    public void A_literal_transform_that_does_not_satisfy_the_guard_blocks()
    {
        var steps = new ReactiveStep[]
        {
            new ReactiveTransform("S", new[] { new ReactiveAssignment("flag", "off") }),
            Guard(FlagOn),
        };
        Assert.False(ReactiveStepProcessor.Passes(steps, Env()));
    }

    [Fact]
    public void A_variable_ref_transform_copies_a_payload_field_for_a_downstream_guard()
    {
        var copyPlateToP = new ReactiveAssignment("p", Json("""{ "__type":"variable_ref", "variableName":"plate" }"""));
        var steps = new ReactiveStep[]
        {
            new ReactiveTransform("S", new[] { copyPlateToP }),
            Guard(PInWhitelist),
        };
        Assert.True(ReactiveStepProcessor.Passes(steps, Env("""{ "plate": "DEF" }""")));
        Assert.False(ReactiveStepProcessor.Passes(steps, Env("""{ "plate": "NOPE" }""")));
    }

    [Fact]
    public void An_unresolved_variable_ref_leaves_the_target_unset_so_the_guard_fails_closed()
    {
        var copyMissing = new ReactiveAssignment("p", Json("""{ "__type":"variable_ref", "variableName":"absent" }"""));
        var steps = new ReactiveStep[]
        {
            new ReactiveTransform("S", new[] { copyMissing }),
            Guard(PInWhitelist),
        };
        Assert.False(ReactiveStepProcessor.Passes(steps, Env("""{ "plate": "DEF" }""")));
    }

    [Fact]
    public void An_expression_valued_transform_is_unsupported_and_leaves_the_variable_unset()
    {
        var exprValue = new ReactiveAssignment("flag", "{{ $node.x.output.y }}");
        var steps = new ReactiveStep[]
        {
            new ReactiveTransform("S", new[] { exprValue }),
            Guard(FlagOn),
        };
        Assert.False(ReactiveStepProcessor.Passes(steps, Env()));
    }

    [Fact]
    public void Guards_short_circuit_in_order()
    {
        // first guard requires flag == on (set by the transform); second is independent and fails
        var steps = new ReactiveStep[]
        {
            new ReactiveTransform("S", new[] { new ReactiveAssignment("flag", "on") }),
            Guard(FlagOn),
            Guard(PInWhitelist), // "p" is never set → fails closed
        };
        Assert.False(ReactiveStepProcessor.Passes(steps, Env("""{ "plate": "DEF" }""")));
    }
}
