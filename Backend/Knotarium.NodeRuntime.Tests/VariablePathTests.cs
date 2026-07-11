using System.Collections.Generic;
using Xunit;
using Knotarium.NodeRuntime;

namespace Knotarium.NodeRuntime.Tests;

public class VariablePathTests
{
    [Fact]
    public void Parse_BareName_HasHeadAndNoSegments()
    {
        Assert.True(VariablePath.TryParse("foo", out var path));
        Assert.Equal("foo", path!.Head);
        Assert.Empty(path.Segments);
    }

    [Fact]
    public void Parse_DottedMember_YieldsMemberSegment()
    {
        Assert.True(VariablePath.TryParse("myDict.name", out var path));
        Assert.Equal("myDict", path!.Head);
        Assert.Equal(new PathSegment[] { new PathSegment.Member("name") }, path.Segments);
    }

    [Fact]
    public void Parse_DoubleQuotedBracketMember_YieldsMemberSegment()
    {
        Assert.True(VariablePath.TryParse("myDict[\"name\"]", out var path));
        Assert.Equal("myDict", path!.Head);
        Assert.Equal(new PathSegment[] { new PathSegment.Member("name") }, path.Segments);
    }

    [Fact]
    public void Parse_SingleQuotedBracketMember_YieldsMemberSegment()
    {
        Assert.True(VariablePath.TryParse("myDict['name']", out var path));
        Assert.Equal("myDict", path!.Head);
        Assert.Equal(new PathSegment[] { new PathSegment.Member("name") }, path.Segments);
    }

    [Fact]
    public void Parse_Index_YieldsIndexSegment()
    {
        Assert.True(VariablePath.TryParse("list[0]", out var path));
        Assert.Equal("list", path!.Head);
        Assert.Equal(new PathSegment[] { new PathSegment.Index(0) }, path.Segments);
    }

    [Fact]
    public void Parse_DeepMixedPath_YieldsSegmentsInOrder()
    {
        Assert.True(VariablePath.TryParse("config.servers[2].host", out var path));
        Assert.Equal("config", path!.Head);
        Assert.Equal(
            new PathSegment[]
            {
                new PathSegment.Member("servers"),
                new PathSegment.Index(2),
                new PathSegment.Member("host"),
            },
            path.Segments);
    }

    [Fact]
    public void Parse_QuotedKeyWithDots_KeepsKeyIntact()
    {
        Assert.True(VariablePath.TryParse("d[\"a.b\"]", out var path));
        Assert.Equal("d", path!.Head);
        Assert.Equal(new PathSegment[] { new PathSegment.Member("a.b") }, path.Segments);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(".name")]      // empty head
    [InlineData("a..b")]       // empty member
    [InlineData("a[0")]        // unterminated bracket
    [InlineData("a[x]")]       // non-integer unquoted index
    [InlineData("a[-1]")]      // negative index
    [InlineData("a[]")]        // empty bracket
    public void Parse_Malformed_ReturnsFalse(string? input)
    {
        Assert.False(VariablePath.TryParse(input, out var path));
        Assert.Null(path);
    }
}
