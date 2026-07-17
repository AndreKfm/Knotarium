// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.NodeRuntime;

public class NodePackageWatcher : IHostedService
{
    private readonly INodeExecutorRegistry _registry;
    private readonly ILogger<NodePackageWatcher> _logger;
    private readonly bool _enabled;
    private readonly string _watchPath;
    private FileSystemWatcher? _watcher;

    public NodePackageWatcher(
        INodeExecutorRegistry registry,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<NodePackageWatcher> logger)
    {
        _registry = registry;
        _logger = logger;

        // Enabled by default in Development environment, or can be explicitly configured
        var devModeConfig = configuration["NodeRuntime:DevMode"];
        if (bool.TryParse(devModeConfig, out var parsedDevMode))
        {
            _enabled = parsedDevMode;
        }
        else
        {
            _enabled = environment.IsDevelopment();
        }

        _watchPath = configuration["NodeRuntime:WatchPath"] ?? Path.Combine(AppContext.BaseDirectory, "nodes");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(_watchPath))
            {
                Directory.CreateDirectory(_watchPath);
            }

            // Initial scan: load binary packages already present on disk. The FileSystemWatcher
            // below only raises events for changes after it starts, so without this scan any
            // package present at boot would never load. This runs in every environment.
            foreach (var dllPath in Directory.EnumerateFiles(_watchPath, "*.dll", SearchOption.AllDirectories))
            {
                await TryLoadPackageAsync(dllPath);
            }

            // Hot-reload (react to file changes) is a development convenience, gated by _enabled.
            if (_enabled)
            {
                _watcher = new FileSystemWatcher(_watchPath, "*.dll")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };

                _watcher.Changed += OnChanged;
                _watcher.Created += OnChanged;
                _watcher.EnableRaisingEvents = true;

                _logger.LogInformation("Node package hot-reload watcher started on directory: {WatchPath}", _watchPath);
            }
            else
            {
                _logger.LogInformation("Node package hot-reload disabled (Prod Mode); initial scan of '{WatchPath}' completed.", _watchPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start NodePackageWatcher on path: {WatchPath}", _watchPath);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _logger.LogInformation("Node package hot-reload watcher stopped.");
        }
        return Task.CompletedTask;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // Run hot-swap loading in a separate thread task to prevent blocking the OS watcher thread
        Task.Run(async () =>
        {
            try
            {
                await TryLoadPackageAsync(e.FullPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception during hot-swap reloading for event {ChangeType} on '{Path}'", e.ChangeType, e.FullPath);
            }
        });
    }

    /// <summary>
    /// Load (or hot-swap) a single binary package: read the DLL + its companion manifest.json
    /// from the same folder and register it into the active executor registry. Shared by the
    /// startup scan and the hot-reload watcher.
    /// </summary>
    private async Task TryLoadPackageAsync(string dllPath)
    {
        var directory = Path.GetDirectoryName(dllPath);
        if (string.IsNullOrEmpty(directory)) return;

        // Find a manifest file in the same folder (manifest.json)
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            _logger.LogWarning("Found package DLL '{DllPath}' but no companion 'manifest.json' exists. Skipping.", dllPath);
            return;
        }

        _logger.LogInformation("Loading compiled package DLL: '{DllPath}'...", dllPath);

        // Load DLL bytes with retry policy to avoid file-locking collision with build systems
        var dllBytes = await ReadFileBytesWithRetryAsync(dllPath);
        if (dllBytes == null)
        {
            _logger.LogError("Failed to read DLL bytes of '{DllPath}' because it remained locked after multiple retries.", dllPath);
            return;
        }

        // Load manifest bytes
        var manifestBytes = await ReadFileBytesWithRetryAsync(manifestPath);
        if (manifestBytes == null)
        {
            _logger.LogError("Failed to read manifest bytes of '{ManifestPath}'.", manifestPath);
            return;
        }

        // Deserialize manifest
        NodePackageManifest manifest;
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            manifest = JsonSerializer.Deserialize<NodePackageManifest>(manifestBytes, options)
                ?? throw new JsonException("Deserialized manifest is null.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse companion manifest: '{ManifestPath}'", manifestPath);
            return;
        }

        // Register in active registry
        try
        {
            _registry.Register(manifest.Id, manifest.Version, dllBytes, manifest);
            _logger.LogInformation("Registered binary package '{PackageId}' version '{Version}' in the active registry.", manifest.Id, manifest.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register binary package from '{DllPath}'.", dllPath);
        }
    }

    private async Task<byte[]?> ReadFileBytesWithRetryAsync(string path, int retries = 5, int delayMs = 150)
    {
        for (int i = 0; i < retries; i++)
        {
            try
            {
                // Use FileShare.None to verify exclusive read access
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bytes = new byte[fs.Length];
                await fs.ReadExactlyAsync(bytes, 0, bytes.Length);
                return bytes;
            }
            catch (IOException)
            {
                // File is currently locked; yield execution thread and try again
                await Task.Delay(delayMs);
            }
        }
        return null;
    }
}
