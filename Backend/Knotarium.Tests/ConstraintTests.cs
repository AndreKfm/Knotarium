using Knotarium.Core.Domain;
using System;
using Xunit;

namespace Knotarium.Tests;

public class ConstraintTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NodeId_Throws_On_Invalid_String_Value(string invalidValue)
    {
        Assert.Throws<ArgumentException>(() => NodeId.Create(invalidValue));
    }

    [Fact]
    public void NodeId_Throws_On_Null_Value()
    {
        Assert.Throws<ArgumentException>(() => NodeId.Create(null!));
    }
}
