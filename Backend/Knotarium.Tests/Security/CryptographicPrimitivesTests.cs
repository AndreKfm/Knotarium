using System;
using System.Collections.Generic;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Security;
using Xunit;

namespace Knotarium.Tests.Security;

public class CryptographicPrimitivesTests
{
    [Fact]
    public void CanonicalJsonSerializer_SortsObjectKeysDeterministically()
    {
        var payload = new
        {
            zeta = 3,
            alpha = new
            {
                beta = 2,
                gamma = 1
            }
        };

        var json = CanonicalJsonSerializer.Serialize(payload);

        Assert.Equal("{\"alpha\":{\"beta\":2,\"gamma\":1},\"zeta\":3}", json);
    }

    [Fact]
    public void AuditHashChain_VerifiesAndDetectsTampering()
    {
        var first = new AuditEntry
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Action = "Publish",
            Actor = "Admin",
            Timestamp = new DateTimeOffset(2026, 5, 25, 10, 0, 0, TimeSpan.Zero),
            Details = "{\"packageId\":\"node-1\"}"
        };

        var second = new AuditEntry
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Action = "Install",
            Actor = "Worker",
            Timestamp = new DateTimeOffset(2026, 5, 25, 10, 1, 0, TimeSpan.Zero),
            Details = "{\"packageId\":\"node-2\"}"
        };

        var rebuilt = AuditHashChain.RebuildChain(new[] { first, second });

        Assert.True(AuditHashChain.VerifyChain(rebuilt));

        rebuilt[1].Details = "{\"packageId\":\"node-3\"}";

        Assert.False(AuditHashChain.VerifyChain(rebuilt));
    }

    [Fact]
    public void PackageSigner_ComputesStableDigest()
    {
        var payload = new PackageSigningPayload(
            "sample-node",
            "1.0.0",
            "Sample Node",
            "Utility",
            "{\"version\":\"1.0.0\"}",
            "source-code",
            new List<string> { "logging", "http" });

        var digest1 = PackageSigner.ComputeDigestHex(payload);
        var digest2 = PackageSigner.ComputeDigestHex(payload);

        Assert.Equal(digest1, digest2);
        Assert.NotEqual(digest1, PackageSigner.ComputeDigestHex(payload with { Source = "other-source" }));
    }
}