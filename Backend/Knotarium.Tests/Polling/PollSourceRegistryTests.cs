using System;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Features.Polling;
using Xunit;

namespace Knotarium.Tests.Polling;

public class PollSourceRegistryTests
{
    private sealed class FakeSource : IPollSource
    {
        public FakeSource(string kind) => Kind = kind;
        public string Kind { get; }
        public Task<PollResult> PollAsync(PollContext c, CancellationToken ct) =>
            Task.FromResult(new PollResult(false, null, null));
    }

    [Fact]
    public void Resolve_ReturnsMatchingSource_CaseInsensitive()
    {
        var registry = new PollSourceRegistry(new IPollSource[] { new FakeSource("http"), new FakeSource("openapi") });
        Assert.Equal("openapi", registry.Resolve("OpenApi").Kind);
    }

    [Fact]
    public void Resolve_UnknownKind_Throws()
    {
        var registry = new PollSourceRegistry(new IPollSource[] { new FakeSource("http") });
        Assert.Throws<InvalidOperationException>((Action)(() => registry.Resolve("ftp")));
    }
}
