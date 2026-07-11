using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.Options;
using Knotarium.Features.Options;
using Xunit;

namespace Knotarium.Tests.Options;

public class ResourceResolverTests
{
    private sealed class StubLoader : IOptionsLoader
    {
        private readonly List<OptionItem> _items;
        public int LoadCount { get; private set; }
        public StubLoader(IEnumerable<OptionItem> items) => _items = items.ToList();
        public string Name => "stub";
        public Task<OptionListResult> LoadAsync(OptionLoadContext context, CancellationToken ct)
        {
            LoadCount++;
            return Task.FromResult(new OptionListResult(_items));
        }
    }

    private sealed class StubRegistry : IOptionsLoaderRegistry
    {
        private readonly IOptionsLoader _loader;
        public StubRegistry(IOptionsLoader loader) => _loader = loader;
        public IOptionsLoader? Get(string name) => name == _loader.Name ? _loader : null;
    }

    private static OptionLoadContext Ctx() => new(null, new Dictionary<string, string>());

    private static ResourceResolver Build(params OptionItem[] live)
        => new(new StubRegistry(new StubLoader(live)));

    [Fact]
    public async Task Resolve_ReorderedList_ResolvesSameTarget()
    {
        var a = new OptionItem("Front Office", "res_7f3a");
        var b = new OptionItem("Warehouse", "res_22b1");

        var resolverOrder1 = Build(a, b);
        var resolverOrder2 = Build(b, a); // upstream reordered

        var r1 = await resolverOrder1.ResolveAsync("stub", Single("res_7f3a"), Ctx(), CancellationToken.None);
        var r2 = await resolverOrder2.ResolveAsync("stub", Single("res_7f3a"), Ctx(), CancellationToken.None);

        Assert.Equal("res_7f3a", Assert.Single(r1.Resources).Value);
        Assert.Equal("res_7f3a", Assert.Single(r2.Resources).Value);
    }

    [Fact]
    public async Task Resolve_DeletedKey_FailsClosed()
    {
        var resolver = Build(new OptionItem("Warehouse", "res_22b1"));
        var ex = await Assert.ThrowsAsync<ResourceResolutionException>(() =>
            resolver.ResolveAsync("stub", Single("res_deleted"), Ctx(), CancellationToken.None));
        Assert.Contains("res_deleted", ex.Message);
        Assert.Equal(new[] { "res_deleted" }, ex.MissingKeys);
    }

    [Fact]
    public async Task Resolve_MultiSelect_OneMissingKey_FailsClosedNamingIt()
    {
        var resolver = Build(
            new OptionItem("A", "a"),
            new OptionItem("C", "c"));
        var ex = await Assert.ThrowsAsync<ResourceResolutionException>(() =>
            resolver.ResolveAsync("stub", Multi("a", "b", "c"), Ctx(), CancellationToken.None));
        Assert.Equal(new[] { "b" }, ex.MissingKeys);
    }

    [Fact]
    public async Task Resolve_MultiSelect_PreservesInputOrder()
    {
        var resolver = Build(
            new OptionItem("A", "a"),
            new OptionItem("B", "b"),
            new OptionItem("C", "c"));
        var result = await resolver.ResolveAsync("stub", Multi("c", "a", "b"), Ctx(), CancellationToken.None);
        Assert.Equal(new[] { "c", "a", "b" }, result.Resources.Select(r => r.Value));
    }

    [Fact]
    public async Task Resolve_AmbiguousNameKey_FailsClosed()
    {
        // Two live resources share the same stable key (a name-typed collection with duplicates).
        var resolver = Build(
            new OptionItem("Front Office", "Front Office"),
            new OptionItem("Front Office", "Front Office"));
        var ex = await Assert.ThrowsAsync<ResourceResolutionException>(() =>
            resolver.ResolveAsync("stub", Single("Front Office"), Ctx(), CancellationToken.None));
        Assert.Equal(new[] { "Front Office" }, ex.AmbiguousKeys);
    }

    [Fact]
    public async Task Resolve_MultiSelect_SingleLiveFetch()
    {
        var loader = new StubLoader(new[] { new OptionItem("A", "a"), new OptionItem("B", "b") });
        var resolver = new ResourceResolver(new StubRegistry(loader));
        await resolver.ResolveAsync("stub", Multi("a", "b"), Ctx(), CancellationToken.None);
        Assert.Equal(1, loader.LoadCount);
    }

    private static object Single(string value) =>
        new Dictionary<string, object> { ["value"] = value, ["label"] = "cached", ["mode"] = "list" };

    private static object Multi(params string[] values) =>
        new Dictionary<string, object>
        {
            ["mode"] = "list",
            ["items"] = values.Select(v => new Dictionary<string, object> { ["value"] = v }).ToList(),
        };
}
