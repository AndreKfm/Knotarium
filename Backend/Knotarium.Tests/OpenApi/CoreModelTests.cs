using System;
using System.Collections.Generic;
using Knotarium.Core.Domain.OpenApi;
using Xunit;

namespace Knotarium.Tests.OpenApi;

public class CoreModelTests
{
    [Fact]
    public void OpenApiSpecId_ToString_ReturnsValue()
    {
        var id = new OpenApiSpecId("petstore");
        Assert.Equal("petstore", id.ToString());
    }

    [Fact]
    public void OpenApiSpecId_Equality_SameValue()
    {
        var a = new OpenApiSpecId("x");
        var b = new OpenApiSpecId("x");
        Assert.Equal(a, b);
    }

    [Fact]
    public void OpenApiSpecId_Equality_DifferentValue()
    {
        var a = new OpenApiSpecId("x");
        var b = new OpenApiSpecId("y");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ImportedSpec_With_ChangesOnlyTargetField()
    {
        var original = new ImportedSpec(
            new OpenApiSpecId("id"),
            "Title",
            "1.0",
            "openapi3.0",
            Array.Empty<string>(),
            Array.Empty<string>(),
            DateTimeOffset.UtcNow,
            1);

        var modified = original with { Title = "New" };

        Assert.Equal("New", modified.Title);
        Assert.Equal(original.Id, modified.Id);
        Assert.Equal(original.SpecVersionNumber, modified.SpecVersionNumber);
        Assert.Equal(original.OriginalFormat, modified.OriginalFormat);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("query")]
    [InlineData("header")]
    [InlineData("cookie")]
    public void ApiParameter_In_Values_AreExpected(string inValue)
    {
        var param = new ApiParameter("name", inValue, true, null, "{}");
        Assert.Equal(inValue, param.In);
    }

    [Fact]
    public void ParsedSpec_Operations_PreservesOrder()
    {
        var ops = new List<ApiOperation>
        {
            new("op1", "GET", "/a", null, Array.Empty<string>(), Array.Empty<ApiParameter>(), null, Array.Empty<string>()),
            new("op2", "POST", "/b", null, Array.Empty<string>(), Array.Empty<ApiParameter>(), null, Array.Empty<string>()),
            new("op3", "PUT", "/c", null, Array.Empty<string>(), Array.Empty<ApiParameter>(), null, Array.Empty<string>()),
        };

        var spec = new Knotarium.Core.Contracts.OpenApi.ParsedSpec(
            new ImportedSpec(new OpenApiSpecId("id"), "T", "1.0", "openapi3.0",
                Array.Empty<string>(), Array.Empty<string>(), DateTimeOffset.UtcNow, 1),
            ops,
            Array.Empty<ApiSchema>(),
            Array.Empty<SecurityScheme>());

        Assert.Equal(3, spec.Operations.Count);
        Assert.Equal("op1", spec.Operations[0].OperationId);
        Assert.Equal("op2", spec.Operations[1].OperationId);
        Assert.Equal("op3", spec.Operations[2].OperationId);
    }
}
