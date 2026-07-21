// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Knotarium.Features.Settings;

namespace Knotarium.Api.Services;

/// <summary>
/// Periodically checks free disk space on the volume holding the database and, when it falls below a
/// threshold, disarms the runtime so no new external/automatic runs start (each run writes many journal
/// rows). Retention is time-based only, so nothing else reacts to the disk approaching full; on a truly
/// full disk SQLite fails writes with SQLITE_FULL (degrade-not-crash), but this guard steps in earlier to
/// pause the write pressure and surface a loud warning. It never auto-rearms — recovering disk and
/// re-arming is a deliberate operator action.
///
/// The policy is read <b>live from <see cref="DiskSpacePolicyStore"/> on every check</b> (and to compute
/// the interval), so an admin editing Settings → Retention takes effect from the next tick without a
/// restart. The store falls back to the "Storage" configuration section (MinFreeSpaceMb /
/// FreeSpaceCheckSeconds, defaults 256 / 60) when nothing is persisted:
/// <list type="bullet">
///   <item><c>MinFreeSpaceMb</c> — pause arming below this many MB free (default 256; 0 disables).</item>
///   <item><c>FreeSpaceCheckSeconds</c> — how often to check (default 60, min 30).</item>
/// </list>
/// </summary>
public sealed class DiskSpaceGuardWorker : BackgroundService
{
    private static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _serviceProvider;
    private readonly RuntimeArmingState _armingState;
    private readonly ILogger<DiskSpaceGuardWorker> _logger;
    private readonly string _monitorPath;
    private bool _trippedByGuard;

    public DiskSpaceGuardWorker(
        IServiceProvider serviceProvider,
        RuntimeArmingState armingState,
        ILogger<DiskSpaceGuardWorker> logger,
        string dataDirectory)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _armingState = armingState ?? throw new ArgumentNullException(nameof(armingState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _monitorPath = string.IsNullOrWhiteSpace(dataDirectory) ? AppContext.BaseDirectory : dataDirectory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Disk-space guard started on '{Path}'; the policy is read live from settings each check "
            + "(editable at Settings → Retention).", _monitorPath);

        // The loop never exits on "guard disabled": the threshold can be turned on at runtime via the UI,
        // and we must pick that up. Each cycle re-reads the interval, waits, then re-reads the threshold.
        while (!stoppingToken.IsCancellationRequested)
        {
            var (minFreeBytes, interval) = await ReadPolicyAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                // First check runs one interval after startup, so short-lived hosts never trip the guard.
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (minFreeBytes <= 0)
            {
                // Guard disabled. Keep looping so a later UI change is picked up.
                continue;
            }

            try
            {
                CheckOnce(minFreeBytes);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Disk-space guard check failed; will retry next interval.");
            }
        }
    }

    private async Task<(long MinFreeBytes, TimeSpan Interval)> ReadPolicyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<DiskSpacePolicyStore>();
            var policy = await store.GetDtoAsync(cancellationToken).ConfigureAwait(false);
            var minFreeBytes = (long)policy.MinFreeSpaceMb * 1024L * 1024L;
            var interval = TimeSpan.FromSeconds(Math.Max(30, policy.FreeSpaceCheckSeconds));
            return (minFreeBytes, interval);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to read the disk-space guard policy; using defaults (256 MB / {Seconds}s).", FallbackInterval.TotalSeconds);
            return (256L * 1024L * 1024L, FallbackInterval);
        }
    }

    private void CheckOnce(long minFreeBytes)
    {
        var free = TryGetFreeBytes();
        if (free is null)
        {
            return;
        }

        if (free.Value < minFreeBytes)
        {
            if (_armingState.IsArmed)
            {
                _armingState.SetArmed(false);
                _trippedByGuard = true;
                _logger.LogCritical(
                    "Free disk space {FreeMb} MB is below the {MinMb} MB threshold — runtime DISARMED to stop new runs. Free up space and re-arm manually.",
                    free.Value / (1024 * 1024), minFreeBytes / (1024 * 1024));
            }
        }
        else if (_trippedByGuard)
        {
            // Space recovered. Do not auto-rearm — just note it so the operator knows it is safe to re-arm.
            _trippedByGuard = false;
            _logger.LogWarning(
                "Free disk space recovered to {FreeMb} MB. The runtime was disarmed by the disk-space guard; re-arm it when ready.",
                free.Value / (1024 * 1024));
        }
    }

    private long? TryGetFreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_monitorPath));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not determine free disk space for '{Path}'.", _monitorPath);
            return null;
        }
    }
}
