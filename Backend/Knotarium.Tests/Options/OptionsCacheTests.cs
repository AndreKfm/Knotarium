// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.Options;
using Knotarium.Features.Options;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Knotarium.Tests.Options;

public class OptionsCacheTests
{
    private sealed class CountingLoader : IOptionsLoader
    {
        public int LoadCount { get; private set; }
        public string Name => "counting";
        public Task<OptionListResult> LoadAsync(OptionLoadContext context, CancellationToken ct)
        {
            LoadCount++;
            return Task.FromResult(new OptionListResult(new[] { new OptionItem("A", "a") }));
        }
    }

    private static OptionsCache NewCache() => new(new MemoryCache(new MemoryCacheOptions()));
    private static OptionLoadContext Ctx(string? conn = "srv1", string? search = null) =>
        new(conn, new Dictionary<string, string> { ["path"] = "things" }, search);

    [Fact]
    public async Task RepeatedLoad_WithinTtl_HitsCache()
    {
        var cache = NewCache();
        var loader = new CountingLoader();

        await cache.GetOrLoadAsync(loader, Ctx(), refresh: false, CancellationToken.None);
        await cache.GetOrLoadAsync(loader, Ctx(), refresh: false, CancellationToken.None);

        Assert.Equal(1, loader.LoadCount);
    }

    [Fact]
    public async Task Refresh_ForcesLiveCall()
    {
        var cache = NewCache();
        var loader = new CountingLoader();

        await cache.GetOrLoadAsync(loader, Ctx(), refresh: false, CancellationToken.None);
        await cache.GetOrLoadAsync(loader, Ctx(), refresh: true, CancellationToken.None);

        Assert.Equal(2, loader.LoadCount);
    }

    [Fact]
    public async Task DifferentSearch_UsesSeparateCacheKey()
    {
        var cache = NewCache();
        var loader = new CountingLoader();

        await cache.GetOrLoadAsync(loader, Ctx(search: "foo"), refresh: false, CancellationToken.None);
        await cache.GetOrLoadAsync(loader, Ctx(search: "bar"), refresh: false, CancellationToken.None);

        Assert.Equal(2, loader.LoadCount);
    }

    [Fact]
    public async Task DifferentConnection_UsesSeparateCacheKey()
    {
        var cache = NewCache();
        var loader = new CountingLoader();

        await cache.GetOrLoadAsync(loader, Ctx(conn: "srvA"), refresh: false, CancellationToken.None);
        await cache.GetOrLoadAsync(loader, Ctx(conn: "srvB"), refresh: false, CancellationToken.None);

        Assert.Equal(2, loader.LoadCount);
    }
}
