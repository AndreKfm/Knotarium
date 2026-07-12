using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Api.Services;

/// <summary>
/// Bounds the size of the run history so the journal (the largest, fastest-growing table) can't grow
/// without limit. Periodically deletes <i>terminal</i> runs older than a configurable window; the
/// database cascades each deleted run to its journal entries and node states. In-flight runs
/// (Pending/Running/Suspended/WaitingForRetry) are never touched, and recent runs stay fully intact for
/// review — only old, finished runs are pruned.
///
/// Configuration (all optional, safe defaults):
/// <list type="bullet">
///   <item><c>Retention:RunHistoryDays</c> — keep terminal runs this many days (default 30). 0 or negative
///   disables pruning entirely (keep forever).</item>
///   <item><c>Retention:SweepIntervalMinutes</c> — how often to sweep (default 60). The first sweep runs one
///   interval after startup, so short-lived processes never prune.</item>
/// </list>
/// </summary>
public sealed class JournalRetentionWorker : BackgroundService
{
    // Finished runs only. In-flight statuses (Pending/Running/Suspended/WaitingForRetry) are excluded so a
    // long-running or suspended run is never deleted out from under the executor.
    private static readonly ExecutionStatus[] TerminalStatuses =
    {
        ExecutionStatus.Completed,
        ExecutionStatus.Failed,
        ExecutionStatus.Cancelled,
        ExecutionStatus.Discarded,
    };

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<JournalRetentionWorker> _logger;
    private readonly int _retentionDays;
    private readonly TimeSpan _sweepInterval;

    public JournalRetentionWorker(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<JournalRetentionWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _retentionDays = configuration.GetValue("Retention:RunHistoryDays", 30);
        var sweepMinutes = configuration.GetValue("Retention:SweepIntervalMinutes", 60);
        _sweepInterval = TimeSpan.FromMinutes(Math.Max(1, sweepMinutes));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_retentionDays <= 0)
        {
            _logger.LogInformation("Journal retention disabled (Retention:RunHistoryDays <= 0); run history is kept indefinitely.");
            return;
        }

        _logger.LogInformation(
            "Journal retention active: pruning terminal runs older than {Days} day(s) every {Minutes} minute(s).",
            _retentionDays, _sweepInterval.TotalMinutes);

        using var timer = new PeriodicTimer(_sweepInterval);

        // First tick fires after one interval — deliberately not on startup, so short-lived hosts (tests,
        // one-shot runs) never prune.
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Journal retention sweep failed; will retry on the next interval.");
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_retentionDays);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Bulk delete; the ExecutionInstance → JournalEntries/NodeStates cascade (ON DELETE CASCADE, enforced
        // by EF's foreign_keys=ON) removes each pruned run's rows in the same statement.
        var deleted = await db.ExecutionInstances
            .Where(e => TerminalStatuses.Contains(e.Status) && e.UpdatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deleted <= 0)
        {
            return;
        }

        _logger.LogInformation("Journal retention pruned {Count} run(s) older than {Days} day(s).", deleted, _retentionDays);

        // Reclaim the space the delete freed in the WAL/database instead of letting the file stay high-water.
        if (db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
        }
    }
}
