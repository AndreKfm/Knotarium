// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Knotarium.Api.Services;

/// <summary>
/// Periodically checks free disk space on the volume holding the database and, when it falls below a
/// threshold, disarms the runtime so no new external/automatic runs start (each run writes many journal
/// rows). Retention is time-based only, so nothing else reacts to the disk approaching full; on a truly
/// full disk SQLite fails writes with SQLITE_FULL (degrade-not-crash), but this guard steps in earlier to
/// pause the write pressure and surface a loud warning. It never auto-rearms — recovering disk and
/// re-arming is a deliberate operator action.
///
/// Configuration (optional):
/// <list type="bullet">
///   <item><c>Storage:MinFreeSpaceMb</c> — pause arming below this many MB free (default 256; 0 disables).</item>
///   <item><c>Storage:FreeSpaceCheckSeconds</c> — how often to check (default 60, min 30).</item>
/// </list>
/// </summary>
public sealed class DiskSpaceGuardWorker : BackgroundService
{
    private readonly RuntimeArmingState _armingState;
    private readonly ILogger<DiskSpaceGuardWorker> _logger;
    private readonly string _monitorPath;
    private readonly long _minFreeBytes;
    private readonly TimeSpan _interval;
    private bool _trippedByGuard;

    public DiskSpaceGuardWorker(
        RuntimeArmingState armingState,
        IConfiguration configuration,
        ILogger<DiskSpaceGuardWorker> logger,
        string dataDirectory)
    {
        _armingState = armingState ?? throw new ArgumentNullException(nameof(armingState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _monitorPath = string.IsNullOrWhiteSpace(dataDirectory) ? AppContext.BaseDirectory : dataDirectory;
        _minFreeBytes = configuration.GetValue("Storage:MinFreeSpaceMb", 256L) * 1024L * 1024L;
        var seconds = configuration.GetValue("Storage:FreeSpaceCheckSeconds", 60);
        _interval = TimeSpan.FromSeconds(Math.Max(30, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_minFreeBytes <= 0)
        {
            _logger.LogInformation("Disk-space guard disabled (Storage:MinFreeSpaceMb <= 0).");
            return;
        }

        _logger.LogInformation(
            "Disk-space guard active: pausing arming below {MinMb} MB free on '{Path}', checked every {Seconds}s.",
            _minFreeBytes / (1024 * 1024), _monitorPath, _interval.TotalSeconds);

        using var timer = new PeriodicTimer(_interval);
        // First tick after one interval, so short-lived hosts (tests, one-shot runs) never trip the guard.
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                CheckOnce();
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

    private void CheckOnce()
    {
        var free = TryGetFreeBytes();
        if (free is null)
        {
            return;
        }

        if (free.Value < _minFreeBytes)
        {
            if (_armingState.IsArmed)
            {
                _armingState.SetArmed(false);
                _trippedByGuard = true;
                _logger.LogCritical(
                    "Free disk space {FreeMb} MB is below the {MinMb} MB threshold — runtime DISARMED to stop new runs. Free up space and re-arm manually.",
                    free.Value / (1024 * 1024), _minFreeBytes / (1024 * 1024));
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
