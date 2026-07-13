using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Security;

namespace Knotarium.Api.Services;

/// <summary>
/// Bounds the size of the tables that otherwise grow without limit, so the database can't fill the disk
/// over a long-running deployment. Runs on a periodic sweep:
/// <list type="bullet">
///   <item><b>Run history</b> — deletes <i>terminal</i> runs older than the window; the database cascades
///   each deleted run to its journal entries, node states, work items, retry states and correlation tokens.
///   In-flight runs (Pending/Running/Suspended/WaitingForRetry) are never touched.</item>
///   <item><b>Schedule fires</b> — the append-only per-tick log (its FK to a run is <c>ON DELETE SET NULL</c>,
///   so run-history retention never reaches it). Pruned by fire time on the same window: a once-a-minute cron
///   adds ~525k rows/year, none of which were previously reclaimed.</item>
///   <item><b>Workflow versions</b> (opt-in) — caps the immutable version history to the N most recent per
///   workflow, but never deletes a version that is active, referenced by the activation log, or referenced by
///   any retained run, so replay/audit lineage of what survives stays intact.</item>
///   <item><b>OpenAPI spec versions</b> (opt-in) — caps re-import history to the N most recent per spec.</item>
///   <item><b>Audit entries</b> (opt-in) — rolls over the tamper-evident audit log older than a window and
///   re-anchors the remaining hash chain so startup verification still passes.</item>
/// </list>
/// After a sweep the SQLite WAL is checkpoint-truncated and (when the file is in incremental auto-vacuum mode)
/// freed pages are returned to the OS via <c>PRAGMA incremental_vacuum</c>.
///
/// Configuration (all optional, safe defaults):
/// <list type="bullet">
///   <item><c>Retention:RunHistoryDays</c> — keep terminal runs + schedule fires this many days (default 30).
///   0 or negative disables time-based pruning.</item>
///   <item><c>Retention:SweepIntervalMinutes</c> — how often to sweep (default 60). The first sweep runs one
///   interval after startup, so short-lived processes never prune.</item>
///   <item><c>Retention:MaxWorkflowVersionsPerWorkflow</c> — cap version history per workflow (default 0 = keep
///   all).</item>
///   <item><c>Retention:MaxOpenApiSpecVersionsPerSpec</c> — cap OpenAPI re-import history per spec (default 0 =
///   keep all).</item>
///   <item><c>Retention:AuditEntryDays</c> — roll over audit entries older than this many days (default 0 =
///   keep forever). Rewrites the remaining chain, so enable only if the boundary tamper-evidence tradeoff is
///   acceptable.</item>
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
    private readonly int _maxWorkflowVersionsPerWorkflow;
    private readonly int _maxOpenApiSpecVersionsPerSpec;
    private readonly int _auditEntryDays;
    private readonly TimeSpan _sweepInterval;

    public JournalRetentionWorker(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<JournalRetentionWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _retentionDays = configuration.GetValue("Retention:RunHistoryDays", 30);
        _maxWorkflowVersionsPerWorkflow = configuration.GetValue("Retention:MaxWorkflowVersionsPerWorkflow", 0);
        _maxOpenApiSpecVersionsPerSpec = configuration.GetValue("Retention:MaxOpenApiSpecVersionsPerSpec", 0);
        _auditEntryDays = configuration.GetValue("Retention:AuditEntryDays", 0);
        var sweepMinutes = configuration.GetValue("Retention:SweepIntervalMinutes", 60);
        _sweepInterval = TimeSpan.FromMinutes(Math.Max(1, sweepMinutes));
    }

    private bool AnyRetentionEnabled =>
        _retentionDays > 0
        || _maxWorkflowVersionsPerWorkflow > 0
        || _maxOpenApiSpecVersionsPerSpec > 0
        || _auditEntryDays > 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!AnyRetentionEnabled)
        {
            _logger.LogInformation("Data retention disabled (no Retention:* limits set); data is kept indefinitely.");
            return;
        }

        _logger.LogInformation(
            "Data retention active: runs+schedule-fires > {Days}d, max {WfVersions} versions/workflow, "
            + "max {SpecVersions} versions/spec, audit > {AuditDays}d; sweeping every {Minutes} minute(s).",
            _retentionDays, _maxWorkflowVersionsPerWorkflow, _maxOpenApiSpecVersionsPerSpec, _auditEntryDays,
            _sweepInterval.TotalMinutes);

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
                _logger.LogError(exception, "Data retention sweep failed; will retry on the next interval.");
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var totalDeleted = 0L;
        if (_retentionDays > 0)
        {
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_retentionDays);
            totalDeleted += await PruneRunHistoryAsync(db, cutoff, cancellationToken).ConfigureAwait(false);
            totalDeleted += await PruneScheduleFiresAsync(db, cutoff, cancellationToken).ConfigureAwait(false);
        }

        if (_maxWorkflowVersionsPerWorkflow > 0)
        {
            totalDeleted += await CapWorkflowVersionsAsync(db, cancellationToken).ConfigureAwait(false);
        }

        if (_maxOpenApiSpecVersionsPerSpec > 0)
        {
            totalDeleted += await CapOpenApiSpecVersionsAsync(db, cancellationToken).ConfigureAwait(false);
        }

        if (_auditEntryDays > 0)
        {
            totalDeleted += await RollOverAuditEntriesAsync(db, cancellationToken).ConfigureAwait(false);
        }

        if (totalDeleted <= 0)
        {
            return;
        }

        // Reclaim the space the deletes freed instead of letting the file stay at high-water.
        if (db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
            // No-op unless the database is in incremental auto-vacuum mode (set at first startup); when it is,
            // this returns freed pages to the OS cheaply, without a full VACUUM rewrite.
            await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<long> PruneRunHistoryAsync(AppDbContext db, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        // Bulk delete; the ExecutionInstance → children cascade (ON DELETE CASCADE, enforced by
        // foreign_keys=ON) removes each pruned run's rows in the same statement.
        var deleted = await db.ExecutionInstances
            .Where(e => TerminalStatuses.Contains(e.Status) && e.UpdatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            _logger.LogInformation("Retention pruned {Count} terminal run(s) older than {Days} day(s).", deleted, _retentionDays);
        }
        return deleted;
    }

    private async Task<long> PruneScheduleFiresAsync(AppDbContext db, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var deleted = await db.ScheduleFires
            .Where(sf => sf.FiredAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            _logger.LogInformation("Retention pruned {Count} schedule-fire record(s) older than {Days} day(s).", deleted, _retentionDays);
        }
        return deleted;
    }

    private async Task<long> CapWorkflowVersionsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        // Versions that must be preserved regardless of age/count: the active version, anything referenced by
        // the append-only activation log (FK, no cascade — deleting would fail), and anything a retained run
        // executed (replay lineage).
        var referenced = new HashSet<Guid>();
        foreach (var id in await db.ActiveWorkflowVersions.AsNoTracking().Select(a => a.WorkflowVersionId).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            referenced.Add(id.Value);
        }
        foreach (var a in await db.WorkflowVersionActivations.AsNoTracking()
                     .Select(a => new { a.WorkflowVersionId, a.RestoredFromVersionId, a.PreviousActiveVersionId })
                     .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            referenced.Add(a.WorkflowVersionId.Value);
            if (a.RestoredFromVersionId is { } r) referenced.Add(r.Value);
            if (a.PreviousActiveVersionId is { } p) referenced.Add(p.Value);
        }
        foreach (var v in await db.ExecutionInstances.AsNoTracking()
                     .Where(e => e.WorkflowVersionId != null)
                     .Select(e => e.WorkflowVersionId).Distinct()
                     .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (v is { } id) referenced.Add(id.Value);
        }

        // Per workflow: keep the newest N by version number; among the rest, delete only the unreferenced ones.
        var versions = await db.WorkflowVersions.AsNoTracking()
            .Select(v => new { v.Id, v.WorkflowDefinitionId, v.VersionNumber })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var toDelete = versions
            .GroupBy(v => v.WorkflowDefinitionId.Value)
            .SelectMany(g => g.OrderByDescending(v => v.VersionNumber).Skip(_maxWorkflowVersionsPerWorkflow))
            .Where(v => !referenced.Contains(v.Id.Value))
            .Select(v => v.Id.Value)
            .ToList();

        if (toDelete.Count == 0)
        {
            return 0;
        }

        var deleted = await db.WorkflowVersions
            .Where(v => toDelete.Contains(v.Id.Value))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            _logger.LogInformation("Retention capped workflow versions: deleted {Count} old unreferenced version(s).", deleted);
        }
        return deleted;
    }

    private async Task<long> CapOpenApiSpecVersionsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var versions = await db.OpenApiSpecVersions.AsNoTracking()
            .Select(v => new { v.RowId, v.SpecId, v.VersionNumber })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var toDelete = versions
            .GroupBy(v => v.SpecId)
            .SelectMany(g => g.OrderByDescending(v => v.VersionNumber).Skip(_maxOpenApiSpecVersionsPerSpec))
            .Select(v => v.RowId)
            .ToList();

        if (toDelete.Count == 0)
        {
            return 0;
        }

        var deleted = await db.OpenApiSpecVersions
            .Where(v => toDelete.Contains(v.RowId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            _logger.LogInformation("Retention capped OpenAPI spec versions: deleted {Count} old version(s).", deleted);
        }
        return deleted;
    }

    private async Task<long> RollOverAuditEntriesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_auditEntryDays);

        var deleted = await db.AuditEntries
            .Where(a => a.Timestamp < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deleted <= 0)
        {
            return 0;
        }

        // Re-anchor the surviving chain so the new first entry links to genesis and startup verification passes.
        var remaining = await db.AuditEntries.ToListAsync(cancellationToken).ConfigureAwait(false);
        AuditHashChain.RebuildChain(remaining);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Retention rolled over {Count} audit entries older than {Days} day(s) and re-anchored the chain.",
            deleted, _auditEntryDays);
        return deleted;
    }
}
