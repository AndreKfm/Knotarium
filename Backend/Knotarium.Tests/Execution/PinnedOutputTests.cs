using System.Collections.Generic;
using System.Text.Json;
using Knotarium.Features.Execution;
using Xunit;

namespace Knotarium.Tests.Execution;

/// <summary>Unit coverage for <see cref="PinnedOutput"/> — the design-time pin property reader.</summary>
public class PinnedOutputTests
{
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Null_or_unknown_shapes_return_null()
    {
        Assert.Null(PinnedOutput.TryReadOutputs(null));
        Assert.Null(PinnedOutput.TryReadOutputs("a string"));
        Assert.Null(PinnedOutput.TryReadOutputs(42));
    }

    [Fact]
    public void Disabled_pin_returns_null_jsonElement()
    {
        Assert.Null(PinnedOutput.TryReadOutputs(Json("""{ "enabled": false, "payload": { "a": 1 } }""")));
        Assert.Null(PinnedOutput.TryReadOutputs(Json("""{ "payload": { "a": 1 } }""")));
    }

    [Fact]
    public void Enabled_jsonElement_wraps_payload_under_default_port()
    {
        var outputs = PinnedOutput.TryReadOutputs(Json("""{ "enabled": true, "payload": { "a": 1 } }"""));
        Assert.NotNull(outputs);
        Assert.True(outputs!.ContainsKey("result"));
        Assert.Equal(JsonValueKind.Object, ((JsonElement)outputs["result"]).ValueKind);
    }

    [Fact]
    public void Enabled_jsonElement_honors_explicit_port()
    {
        var outputs = PinnedOutput.TryReadOutputs(Json("""{ "enabled": true, "port": "true", "payload": 7 }"""));
        Assert.NotNull(outputs);
        Assert.True(outputs!.ContainsKey("true"));
        Assert.False(outputs.ContainsKey("result"));
    }

    [Fact]
    public void Enabled_dictionary_shape_is_supported()
    {
        var raw = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["port"] = "result",
            ["payload"] = new Dictionary<string, object> { ["k"] = "v" },
        };
        var outputs = PinnedOutput.TryReadOutputs(raw);
        Assert.NotNull(outputs);
        Assert.True(outputs!.ContainsKey("result"));
    }

    [Fact]
    public void Disabled_dictionary_returns_null()
    {
        var raw = new Dictionary<string, object> { ["enabled"] = false, ["payload"] = "x" };
        Assert.Null(PinnedOutput.TryReadOutputs(raw));
    }
}
