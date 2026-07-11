using System;
using Knotarium.Features.Bundles;
using Xunit;

namespace Knotarium.Tests.Bundles;

public class BundleVersioningTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v1.2.3", 1, 2, 3, null)]
    [InlineData("1.2", 1, 2, 0, null)]
    [InlineData("1", 1, 0, 0, null)]
    [InlineData("1.2.3-rc.1", 1, 2, 3, "rc.1")]
    [InlineData("1.2.3+build.7", 1, 2, 3, null)]
    [InlineData("1.2.3-rc.1+build.7", 1, 2, 3, "rc.1")]
    public void TryParse_ParsesCoreAndPreRelease(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(SemanticVersion.TryParse(text, out var v));
        Assert.Equal(new SemanticVersion(major, minor, patch, pre), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.0")]
    [InlineData("1.2.3-")]
    public void TryParse_RejectsInvalid(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Fact]
    public void Compare_OrdersCoreThenPreRelease()
    {
        Assert.True(SemanticVersion.Parse("1.0.0").CompareTo(SemanticVersion.Parse("1.0.1")) < 0);
        Assert.True(SemanticVersion.Parse("2.0.0").CompareTo(SemanticVersion.Parse("1.9.9")) > 0);
        // A pre-release has lower precedence than its release.
        Assert.True(SemanticVersion.Parse("1.0.0-rc.1").CompareTo(SemanticVersion.Parse("1.0.0")) < 0);
        // Numeric pre-release identifiers compare numerically; fewer identifiers sort lower.
        Assert.True(SemanticVersion.Parse("1.0.0-rc.2").CompareTo(SemanticVersion.Parse("1.0.0-rc.10")) < 0);
        Assert.True(SemanticVersion.Parse("1.0.0-alpha").CompareTo(SemanticVersion.Parse("1.0.0-alpha.1")) < 0);
    }

    [Theory]
    [InlineData("*", "1.2.3", true)]
    [InlineData("any", "0.0.1", true)]
    [InlineData("", "9.9.9", true)]
    [InlineData(">=1.0.0", "1.0.0", true)]
    [InlineData(">=1.0.0", "0.9.9", false)]
    [InlineData(">1.0.0", "1.0.0", false)]
    [InlineData("<=2.0.0", "2.0.0", true)]
    [InlineData("<2.0.0", "2.0.0", false)]
    [InlineData("=1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    public void Constraint_ComparatorsAndPins(string constraint, string version, bool expected)
    {
        Assert.Equal(expected, VersionConstraint.Parse(constraint).IsSatisfiedBy(SemanticVersion.Parse(version)));
    }

    [Theory]
    // ^1.2.3 => >=1.2.3 <2.0.0
    [InlineData("^1.2.3", "1.2.3", true)]
    [InlineData("^1.2.3", "1.9.0", true)]
    [InlineData("^1.2.3", "2.0.0", false)]
    [InlineData("^1.2.3", "1.2.2", false)]
    // ^0.2.3 => >=0.2.3 <0.3.0
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    // ^0.0.3 => >=0.0.3 <0.0.4
    [InlineData("^0.0.3", "0.0.3", true)]
    [InlineData("^0.0.3", "0.0.4", false)]
    // ~1.2.3 => >=1.2.3 <1.3.0
    [InlineData("~1.2.3", "1.2.9", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    public void Constraint_CaretAndTilde(string constraint, string version, bool expected)
    {
        Assert.Equal(expected, VersionConstraint.Parse(constraint).IsSatisfiedBy(SemanticVersion.Parse(version)));
    }
}
