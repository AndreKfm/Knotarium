using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.Options;
using Microsoft.Extensions.Caching.Memory;

namespace Knotarium.Features.Options;

/// <summary>
/// Short-TTL cache in front of design-time option loads so the editor doesn't hammer the external
/// API every time a node opens. Keyed by <c>(connectionId, loaderName, hash(dependsOn), search)</c>.
/// Only successful results are cached; failures propagate (and stay uncached) so a transient outage
/// isn't pinned for the whole TTL. A manual refresh busts the entry.
/// </summary>
public sealed class OptionsCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(45);
    private readonly IMemoryCache _cache;

    public OptionsCache(IMemoryCache cache) => _cache = cache;

    public async Task<OptionListResult> GetOrLoadAsync(
        IOptionsLoader loader,
        OptionLoadContext context,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var key = BuildKey(loader.Name, context);

        if (refresh)
        {
            _cache.Remove(key);
        }
        else if (_cache.TryGetValue(key, out OptionListResult? cached) && cached is not null)
        {
            return cached;
        }

        var result = await loader.LoadAsync(context, cancellationToken);
        _cache.Set(key, result, Ttl);
        return result;
    }

    private static string BuildKey(string loaderName, OptionLoadContext context)
    {
        // Order-independent, stable hash of the dependsOn dict.
        var dependsOn = context.DependsOn ?? new Dictionary<string, string>();
        var depsHash = string.Join("&", dependsOn
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));

        return $"options::{loaderName}::{context.ConnectionId ?? "-"}::{depsHash}::{context.Search ?? "-"}";
    }
}
