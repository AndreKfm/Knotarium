using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using Knotarium.NodeRuntime;

namespace Knotarium.NodeRuntime.Tests;

public class VariableTreeTests
{
    private static JsonElement Json(string json)
        => JsonSerializer.Deserialize<JsonElement>(json);

    private static List<PathSegment> Path(params PathSegment[] segs) => new(segs);

    // --- ToMutable ---

    [Fact]
    public void ToMutable_Object_BecomesDictionary()
    {
        var result = VariableTree.ToMutable(Json("{\"a\":1,\"b\":\"x\"}"));
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal(2, dict.Count);
        Assert.Equal("x", dict["b"]);
    }

    [Fact]
    public void ToMutable_Array_BecomesList()
    {
        var result = VariableTree.ToMutable(Json("[1,2,3]"));
        var list = Assert.IsType<List<object?>>(result);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void ToMutable_NestedObjectInArray_IsRecursive()
    {
        var result = VariableTree.ToMutable(Json("{\"servers\":[{\"host\":\"h\"}]}"));
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        var list = Assert.IsType<List<object?>>(dict["servers"]);
        var inner = Assert.IsType<Dictionary<string, object?>>(list[0]);
        Assert.Equal("h", inner["host"]);
    }

    // --- Set: auto-vivification from absent root ---

    [Fact]
    public void Set_AbsentRoot_MemberSegment_CreatesObject()
    {
        var root = VariableTree.Set(null, Path(new PathSegment.Member("name")), 1);
        var dict = Assert.IsType<Dictionary<string, object?>>(root);
        Assert.Equal(1, dict["name"]);
    }

    [Fact]
    public void Set_AbsentRoot_IndexSegment_CreatesArray()
    {
        var root = VariableTree.Set(null, Path(new PathSegment.Index(0)), "v");
        var list = Assert.IsType<List<object?>>(root);
        Assert.Equal("v", Assert.Single(list));
    }

    [Fact]
    public void Set_NestedPath_AutoCreatesIntermediates()
    {
        var root = VariableTree.Set(null, Path(new PathSegment.Member("a"), new PathSegment.Member("b")), 5);
        var dict = Assert.IsType<Dictionary<string, object?>>(root);
        var inner = Assert.IsType<Dictionary<string, object?>>(dict["a"]);
        Assert.Equal(5, inner["b"]);
    }

    [Fact]
    public void Set_MixedNestedPath_CreatesArrayAndObject()
    {
        var root = VariableTree.Set(
            null,
            Path(new PathSegment.Member("servers"), new PathSegment.Index(0), new PathSegment.Member("host")),
            "h");
        var dict = Assert.IsType<Dictionary<string, object?>>(root);
        var list = Assert.IsType<List<object?>>(dict["servers"]);
        var inner = Assert.IsType<Dictionary<string, object?>>(list[0]);
        Assert.Equal("h", inner["host"]);
    }

    // --- Set: preserve siblings / overwrite leaf ---

    [Fact]
    public void Set_PreservesSiblingKeys()
    {
        var root = VariableTree.ToMutable(Json("{\"a\":1}"));
        var result = VariableTree.Set(root, Path(new PathSegment.Member("b")), 2);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal(2, dict.Count);
        Assert.Equal(2, dict["b"]);
        Assert.True(dict.ContainsKey("a"));
    }

    [Fact]
    public void Set_OverwritesExistingLeaf()
    {
        var root = VariableTree.ToMutable(Json("{\"a\":1}"));
        var result = VariableTree.Set(root, Path(new PathSegment.Member("a")), 9);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal(9, dict["a"]);
    }

    [Fact]
    public void Set_ArrayAppendAtLength_Appends()
    {
        var root = VariableTree.ToMutable(Json("{\"list\":[10]}"));
        var result = VariableTree.Set(root, Path(new PathSegment.Member("list"), new PathSegment.Index(1)), 20);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        var list = Assert.IsType<List<object?>>(dict["list"]);
        Assert.Equal(2, list.Count);
        Assert.Equal(20, list[1]);
    }

    // --- Set: conflicts -> throw ---

    [Fact]
    public void Set_ScalarRoot_WithMemberPath_Throws()
    {
        Assert.Throws<VariableTreeException>(
            () => VariableTree.Set(42, Path(new PathSegment.Member("a")), 1));
    }

    [Fact]
    public void Set_IndexIntoExistingObject_Throws()
    {
        var root = VariableTree.ToMutable(Json("{\"a\":{}}"));
        Assert.Throws<VariableTreeException>(
            () => VariableTree.Set(root, Path(new PathSegment.Member("a"), new PathSegment.Index(0)), 1));
    }

    [Fact]
    public void Set_MemberIntoExistingArray_Throws()
    {
        var root = VariableTree.ToMutable(Json("{\"a\":[]}"));
        Assert.Throws<VariableTreeException>(
            () => VariableTree.Set(root, Path(new PathSegment.Member("a"), new PathSegment.Member("x")), 1));
    }

    [Fact]
    public void Set_ArrayIndexBeyondLength_PadsWithNulls()
    {
        var root = VariableTree.ToMutable(Json("{\"list\":[10]}"));
        var result = VariableTree.Set(root, Path(new PathSegment.Member("list"), new PathSegment.Index(5)), 1);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        var list = Assert.IsType<List<object?>>(dict["list"]);
        Assert.Equal(6, list.Count);
        Assert.Null(list[1]);
        Assert.Null(list[4]);
        Assert.Equal(1, list[5]);
    }

    [Fact]
    public void Set_AbsentRoot_IndexBeyondZero_PadsWithNulls()
    {
        var root = VariableTree.Set(null, Path(new PathSegment.Index(2)), "x");
        var list = Assert.IsType<List<object?>>(root);
        Assert.Equal(3, list.Count);
        Assert.Null(list[0]);
        Assert.Null(list[1]);
        Assert.Equal("x", list[2]);
    }
}
