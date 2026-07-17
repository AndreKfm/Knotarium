// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Knotarium.NodeRuntime;

/// <summary>
/// Load context for a binary host-service plugin. Unlike node packages (loaded from bytes into a
/// collectible, fully-isolated context), a plugin must resolve its OWN dependencies — vendor SDK
/// managed wrappers and their native x64 DLLs — from its own folder, while still SHARING the host's
/// contract assemblies so types like <c>IExternalSignalProvider</c> unify across the seam.
///
/// Strategy: assemblies the host already provides (Knotarium.*, Microsoft.Extensions.*, System.*)
/// are delegated to the default context (return null → unified identity); everything else resolves
/// from the plugin's deps.json / folder. Long-lived (non-collectible): a plugin runs for the whole
/// host lifetime.
/// </summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDir;

    public PluginAssemblyLoadContext(string pluginMainDllPath)
        : base("Plugin_" + Path.GetFileNameWithoutExtension(pluginMainDllPath), isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginMainDllPath);
        _pluginDir = Path.GetDirectoryName(pluginMainDllPath)!;

        // Put the plugin folder on the native DLL search path. We resolve the directly-requested native
        // (LoadUnmanagedDll below), but a vendor native then loads its OWN siblings via the OS loader,
        // which does NOT search the plugin folder by default — so the first call into the SDK faults with
        // an SEHException. Registering the folder (AddDllDirectory + LOAD_LIBRARY_SEARCH_* default) lets
        // those transitive native dependencies resolve. Windows-only; best-effort elsewhere.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
                AddDllDirectory(_pluginDir);
            }
            catch { /* older OS without these APIs — fall back to SetDllDirectory */ try { SetDllDirectory(_pluginDir); } catch { } }
        }
    }

    private const int LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(int directoryFlags);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? lpPathName);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name ?? string.Empty;

        // Share host-provided contract/framework assemblies → return null so the DEFAULT context
        // supplies the single canonical copy and type identity is preserved across the seam.
        if (IsHostShared(name))
        {
            return null;
        }

        var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
        if (resolved != null)
        {
            return LoadFromAssemblyPath(resolved);
        }

        // Fallback: a sibling DLL in the plugin folder (SDKs that ship without a complete deps.json).
        var sibling = Path.Combine(_pluginDir, name + ".dll");
        if (File.Exists(sibling))
        {
            return LoadFromAssemblyPath(sibling);
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (resolved != null)
        {
            return LoadUnmanagedDllFromPath(resolved);
        }

        // Probe the plugin folder for the native DLL by common name shapes.
        foreach (var candidate in new[] { unmanagedDllName, unmanagedDllName + ".dll" })
        {
            var path = Path.Combine(_pluginDir, candidate);
            if (File.Exists(path))
            {
                return LoadUnmanagedDllFromPath(path);
            }
        }

        return IntPtr.Zero;
    }

    private static bool IsHostShared(string assemblyName)
        => assemblyName.StartsWith("Knotarium.", StringComparison.OrdinalIgnoreCase)
           || assemblyName.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase)
           || assemblyName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
           || assemblyName.Equals("System", StringComparison.OrdinalIgnoreCase)
           || assemblyName.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
           || assemblyName.Equals("mscorlib", StringComparison.OrdinalIgnoreCase);
}
