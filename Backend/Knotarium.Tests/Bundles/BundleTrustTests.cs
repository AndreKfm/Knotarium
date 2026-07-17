// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Features.Bundles;
using Xunit;

namespace Knotarium.Tests.Bundles;

public class BundleTrustTests
{
    [Theory]
    [InlineData(true, "official", PackageTrustLevel.Verified)]
    [InlineData(true, "local", PackageTrustLevel.Verified)]
    [InlineData(true, "", PackageTrustLevel.Verified)]
    [InlineData(false, "local", PackageTrustLevel.Provisional)]
    [InlineData(false, "LOCAL", PackageTrustLevel.Provisional)]
    [InlineData(false, " local ", PackageTrustLevel.Provisional)]
    [InlineData(false, "official", PackageTrustLevel.Untrusted)]
    [InlineData(false, "registry.example.com", PackageTrustLevel.Untrusted)]
    [InlineData(false, "", PackageTrustLevel.Untrusted)]
    [InlineData(false, null, PackageTrustLevel.Untrusted)]
    public void Derive_MapsSignatureAndSource(bool signatureVerified, string? source, PackageTrustLevel expected)
    {
        Assert.Equal(expected, BundleTrust.Derive(signatureVerified, source));
    }

    [Theory]
    [InlineData(PackageTrustLevel.Verified, false, true)]
    [InlineData(PackageTrustLevel.Verified, true, true)]
    [InlineData(PackageTrustLevel.Provisional, true, true)]
    [InlineData(PackageTrustLevel.Provisional, false, false)]
    [InlineData(PackageTrustLevel.Untrusted, true, false)]
    [InlineData(PackageTrustLevel.Untrusted, false, false)]
    public void IsInstallable_GatesProvisionalBehindOptIn(PackageTrustLevel level, bool allowProvisional, bool expected)
    {
        Assert.Equal(expected, BundleTrust.IsInstallable(level, allowProvisional));
    }

    [Fact]
    public void Token_RoundTrips()
    {
        foreach (var level in System.Enum.GetValues<PackageTrustLevel>())
        {
            Assert.Equal(level, BundleTrust.ParseToken(BundleTrust.ToToken(level)));
        }
    }

    [Fact]
    public void ToToken_MatchesLockSampleValue()
    {
        // The Step 01 lock sample carries "Provisional"; keep the token wire-compatible.
        Assert.Equal("Provisional", BundleTrust.ToToken(PackageTrustLevel.Provisional));
    }

    [Theory]
    [InlineData("verified", PackageTrustLevel.Verified)]
    [InlineData("PROVISIONAL", PackageTrustLevel.Provisional)]
    [InlineData("bogus", PackageTrustLevel.Untrusted)]
    [InlineData("", PackageTrustLevel.Untrusted)]
    [InlineData(null, PackageTrustLevel.Untrusted)]
    [InlineData("5", PackageTrustLevel.Untrusted)]
    public void ParseToken_FailsClosedOnUnknown(string? token, PackageTrustLevel expected)
    {
        Assert.Equal(expected, BundleTrust.ParseToken(token));
    }
}
