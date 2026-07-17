// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.NodeRuntime;

public record RegisteredExecutor(
    INodeExecutor Executor,
    CollectibleAssemblyLoadContext LoadContext,
    NodePackageManifest Manifest
);

public interface INodeExecutorRegistry
{
    void Register(NodePackageId packageId, string version, byte[] assemblyBytes, NodePackageManifest manifest);
    RegisteredExecutor? GetExecutor(NodePackageId packageId, string version);
    bool Unregister(NodePackageId packageId, string version);

    /// <summary>All currently-loaded binary packages (one entry per registered id+version).</summary>
    IReadOnlyCollection<RegisteredExecutor> GetAll();

    /// <summary>The highest-version loaded executor for an id, or null if none is loaded.</summary>
    RegisteredExecutor? GetLatest(NodePackageId packageId);
}

public class NodeExecutorRegistry : INodeExecutorRegistry
{
    private readonly ConcurrentDictionary<(NodePackageId PackageId, string Version), RegisteredExecutor> _executors = new();

    public void Register(NodePackageId packageId, string version, byte[] assemblyBytes, NodePackageManifest manifest)
    {
        if (assemblyBytes == null || assemblyBytes.Length == 0)
        {
            throw new ArgumentException("Assembly bytes cannot be null or empty.", nameof(assemblyBytes));
        }

        var alcName = $"NodePackage_{packageId.Value}_{version}_{Guid.NewGuid():N}";
        var alc = new CollectibleAssemblyLoadContext(alcName);

        Assembly assembly;
        try
        {
            assembly = alc.LoadFromBytes(assemblyBytes);
        }
        catch (Exception ex)
        {
            alc.Unload();
            throw new InvalidOperationException($"Failed to load assembly bytes into CollectibleAssemblyLoadContext: {ex.Message}", ex);
        }

        var executorType = assembly.GetTypes()
            .FirstOrDefault(t => typeof(INodeExecutor).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        if (executorType == null)
        {
            alc.Unload();
            throw new InvalidOperationException($"No class implementing INodeExecutor found in the assembly for package '{packageId}' version '{version}'.");
        }

        INodeExecutor executor;
        try
        {
            executor = (INodeExecutor)Activator.CreateInstance(executorType)!;
        }
        catch (Exception ex)
        {
            alc.Unload();
            throw new InvalidOperationException($"Failed to instantiate executor type '{executorType.FullName}': {ex.Message}", ex);
        }

        var newEntry = new RegisteredExecutor(executor, alc, manifest);
        var key = (packageId, version);

        // Atomically replace and retrieve old entry
        if (_executors.TryGetValue(key, out var oldEntry))
        {
            _executors[key] = newEntry;
            oldEntry.LoadContext.Unload();
        }
        else
        {
            _executors.TryAdd(key, newEntry);
        }
    }

    public RegisteredExecutor? GetExecutor(NodePackageId packageId, string version)
    {
        return _executors.TryGetValue((packageId, version), out var entry) ? entry : null;
    }

    public IReadOnlyCollection<RegisteredExecutor> GetAll() => _executors.Values.ToArray();

    public RegisteredExecutor? GetLatest(NodePackageId packageId)
    {
        return _executors
            .Where(kvp => kvp.Key.PackageId == packageId)
            .OrderByDescending(kvp => kvp.Key.Version, VersionComparer.Instance)
            .Select(kvp => kvp.Value)
            .FirstOrDefault();
    }

    // Orders version strings semantically when parseable (1.10.0 > 1.9.0), else ordinally.
    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (Version.TryParse(x, out var vx) && Version.TryParse(y, out var vy))
            {
                return vx.CompareTo(vy);
            }
            return string.CompareOrdinal(x, y);
        }
    }

    public bool Unregister(NodePackageId packageId, string version)
    {
        var key = (packageId, version);
        if (_executors.TryRemove(key, out var entry))
        {
            entry.LoadContext.Unload();
            return true;
        }
        return false;
    }
}
