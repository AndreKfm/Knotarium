using System;
using System.Collections.Generic;
using KnotGarden.Core.Contracts.Options;

namespace KnotGarden.Features.Options;

/// <summary>
/// DI-backed <see cref="IOptionsLoaderRegistry"/>. Indexes every registered <see cref="IOptionsLoader"/>
/// by its <see cref="IOptionsLoader.Name"/>. Only names present here are invokable — this is the
/// design-time allowlist enforced by the options endpoint.
/// </summary>
public sealed class OptionsLoaderRegistry : IOptionsLoaderRegistry
{
    private readonly IReadOnlyDictionary<string, IOptionsLoader> _loaders;

    public OptionsLoaderRegistry(IEnumerable<IOptionsLoader> loaders)
    {
        var map = new Dictionary<string, IOptionsLoader>(StringComparer.OrdinalIgnoreCase);
        foreach (var loader in loaders)
        {
            // Last registration wins; duplicate names are a configuration error but we keep the
            // registry usable rather than throwing during container build.
            map[loader.Name] = loader;
        }
        _loaders = map;
    }

    public IOptionsLoader? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return _loaders.TryGetValue(name, out var loader) ? loader : null;
    }
}
