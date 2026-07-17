// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text.Json;
using Knotarium.Features.Options;
using Xunit;

namespace Knotarium.Tests.Options;

public class StoredOptionValueTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public void ReadValues_SingleObject_ReturnsValueIgnoringLabel()
    {
        var stored = Json("""{ "value": "res_7f3a", "label": "Front Office", "mode": "list" }""");
        var values = StoredOptionValue.ReadValues(stored);
        Assert.Equal(new[] { "res_7f3a" }, values);
    }

    [Fact]
    public void ReadValues_MultiObject_PreservesOrder()
    {
        var stored = Json("""
            { "mode": "list", "items": [
              { "value": "a", "label": "Apple" },
              { "value": "b", "label": "Banana" }
            ] }
            """);
        var values = StoredOptionValue.ReadValues(stored);
        Assert.Equal(new[] { "a", "b" }, values);
    }

    [Fact]
    public void ReadValues_BareString_TreatedAsSingleValue()
    {
        var values = StoredOptionValue.ReadValues("legacy-id");
        Assert.Equal(new[] { "legacy-id" }, values);
    }

    [Fact]
    public void ReadValues_BareArray_OfStringsAndObjects()
    {
        var stored = Json("""[ "x", { "value": "y", "label": "Why" } ]""");
        var values = StoredOptionValue.ReadValues(stored);
        Assert.Equal(new[] { "x", "y" }, values);
    }

    [Fact]
    public void ReadValues_Null_ReturnsEmpty()
    {
        Assert.Empty(StoredOptionValue.ReadValues((object?)null));
    }

    [Fact]
    public void ReadSingleValue_ReturnsFirstKey()
    {
        var stored = Json("""{ "mode": "list", "items": [ { "value": "first" }, { "value": "second" } ] }""");
        Assert.Equal("first", StoredOptionValue.ReadSingleValue(stored));
    }

    [Fact]
    public void ReadValues_BoxedDictionary_NormalizesAndReads()
    {
        // Simulates a runtime-boxed value rather than a JsonElement.
        var stored = new Dictionary<string, object> { ["value"] = "boxed-id", ["label"] = "Boxed", ["mode"] = "list" };
        Assert.Equal(new[] { "boxed-id" }, StoredOptionValue.ReadValues(stored));
    }
}
