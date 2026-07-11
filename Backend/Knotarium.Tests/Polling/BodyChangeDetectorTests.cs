using Knotarium.Core.Contracts;
using Knotarium.Features.Polling;
using Xunit;

namespace Knotarium.Tests.Polling;

public class BodyChangeDetectorTests
{
    [Fact]
    public void Hash_SameBody_NotNew()
    {
        var first = BodyChangeDetector.Detect(PollChangeDetection.Hash, "{\"a\":1}", cursor: null, jsonPath: null);
        var second = BodyChangeDetector.Detect(PollChangeDetection.Hash, "{\"a\":1}", cursor: first.NewCursor, jsonPath: null);

        Assert.True(first.HasNew);
        Assert.False(second.HasNew);
        Assert.Equal(first.NewCursor, second.NewCursor);
        Assert.Null(second.Payload); // unchanged => no payload surfaced
    }

    [Fact]
    public void Hash_ChangedBody_IsNew()
    {
        var first = BodyChangeDetector.Detect(PollChangeDetection.Hash, "{\"a\":1}", cursor: null, jsonPath: null);
        var second = BodyChangeDetector.Detect(PollChangeDetection.Hash, "{\"a\":2}", cursor: first.NewCursor, jsonPath: null);

        Assert.True(second.HasNew);
        Assert.NotEqual(first.NewCursor, second.NewCursor);
        Assert.Equal("{\"a\":2}", second.Payload); // changed => body surfaced as payload
    }

    [Fact]
    public void JsonCursor_AdvancesOnLargerValue()
    {
        var first = BodyChangeDetector.Detect(PollChangeDetection.JsonCursor, "{\"id\":10}", cursor: null, jsonPath: "id");
        var same = BodyChangeDetector.Detect(PollChangeDetection.JsonCursor, "{\"id\":10}", cursor: first.NewCursor, jsonPath: "id");
        var advanced = BodyChangeDetector.Detect(PollChangeDetection.JsonCursor, "{\"id\":11}", cursor: first.NewCursor, jsonPath: "id");

        Assert.True(first.HasNew);
        Assert.Equal("10", first.NewCursor);
        Assert.False(same.HasNew);
        Assert.True(advanced.HasNew);
        Assert.Equal("11", advanced.NewCursor);
    }

    [Fact]
    public void JsonCursor_NestedPath()
    {
        var result = BodyChangeDetector.Detect(
            PollChangeDetection.JsonCursor, "{\"meta\":{\"latest\":\"2026-06-14\"}}", cursor: null, jsonPath: "meta.latest");
        Assert.True(result.HasNew);
        Assert.Equal("2026-06-14", result.NewCursor);
    }

    [Fact]
    public void Always_IsAlwaysNew()
    {
        var result = BodyChangeDetector.Detect(PollChangeDetection.Always, "anything", cursor: "anything", jsonPath: null);
        Assert.True(result.HasNew);
    }

    [Fact]
    public void JsonCursor_MissingPath_NoChange()
    {
        // The "never flood runs" guarantee: a path that isn't present yields no new data.
        var result = BodyChangeDetector.Detect(
            PollChangeDetection.JsonCursor, "{\"other\":1}", cursor: "prev", jsonPath: "id");

        Assert.False(result.HasNew);
        Assert.Null(result.Payload);
        Assert.Equal("prev", result.NewCursor); // cursor preserved
    }

    [Fact]
    public void JsonCursor_MalformedJson_NoChange()
    {
        var result = BodyChangeDetector.Detect(
            PollChangeDetection.JsonCursor, "not json at all", cursor: "prev", jsonPath: "id");

        Assert.False(result.HasNew);
        Assert.Equal("prev", result.NewCursor);
    }

    [Fact]
    public void JsonCursor_NonNumericChange_IsNew()
    {
        var result = BodyChangeDetector.Detect(
            PollChangeDetection.JsonCursor, "{\"tag\":\"v2\"}", cursor: "v1", jsonPath: "tag");

        Assert.True(result.HasNew);
        Assert.Equal("v2", result.NewCursor);
    }

    [Fact]
    public void Detect_TransportStrategy_Throws()
    {
        Assert.Throws<System.InvalidOperationException>(() =>
            BodyChangeDetector.Detect(PollChangeDetection.Etag, "{}", cursor: null, jsonPath: null));
    }
}
