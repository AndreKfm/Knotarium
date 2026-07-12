using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Knotarium.Infrastructure.Persistence;

/// <summary>
/// One SQLite tuning policy shared by every connection — EF's and the raw execution-journal writer's —
/// so the single-writer execution model runs on the fast, contention-tolerant configuration:
/// <list type="bullet">
///   <item><b>WAL</b> (write-ahead log): readers (the dashboard/SSE timeline) no longer block the writer
///   and vice-versa, and appends are cheaper than rewriting the rollback journal. WAL is a persistent
///   property of the database <i>file</i>, so it is switched on once at startup.</item>
///   <item><b>synchronous=NORMAL</b>: safe under WAL (a crash can lose only the last transaction, never
///   corrupt the file) and removes the per-commit <c>fsync</c> — the dominant cost when every run writes
///   many journal rows. This is a <i>per-connection</i> setting.</item>
///   <item><b>busy_timeout</b>: a momentary lock waits up to the timeout instead of failing immediately
///   with "database is locked". Per-connection.</item>
/// </list>
/// EF also issues <c>PRAGMA foreign_keys=ON</c> on its own connections; nothing here changes that.
/// </summary>
public static class SqlitePragmas
{
    /// <summary>How long a blocked statement waits for a lock before erroring, in milliseconds.</summary>
    public const int BusyTimeoutMs = 5000;

    private static readonly string ConnectionPragmaSql = $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA synchronous=NORMAL;";

    /// <summary>Apply the per-connection tuning pragmas (idempotent; cheap enough to run on every open).</summary>
    public static void ApplyConnectionPragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ConnectionPragmaSql;
        command.ExecuteNonQuery();
    }

    /// <inheritdoc cref="ApplyConnectionPragmas"/>
    public static async Task ApplyConnectionPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ConnectionPragmaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Switch the database file to WAL journalling. Persists in the file header, so a single call (at
    /// startup, on the shared file) covers every later connection. Accepts a <see cref="DbConnection"/>
    /// so it can be called with EF's own connection.
    /// </summary>
    public static void EnableWal(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// Applies <see cref="SqlitePragmas.ApplyConnectionPragmas"/> to every EF SQLite connection as it opens,
/// so EF-issued queries get the same busy_timeout/synchronous tuning as the raw journal writer.
/// </summary>
public sealed class SqliteTuningConnectionInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is SqliteConnection sqlite)
        {
            SqlitePragmas.ApplyConnectionPragmas(sqlite);
        }

        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (connection is SqliteConnection sqlite)
        {
            await SqlitePragmas.ApplyConnectionPragmasAsync(sqlite, cancellationToken);
        }

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }
}
