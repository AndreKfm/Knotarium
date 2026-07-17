// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
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

    /// <summary>
    /// Upper bound the WAL is truncated back to after a checkpoint, in bytes (64 MiB). Without this the
    /// <c>-wal</c> file can stay at its high-water mark after a burst of writes even once checkpointed; with
    /// it, SQLite shrinks the WAL back down, so it can't slowly consume disk under sustained read load.
    /// </summary>
    public const long WalSizeLimitBytes = 64L * 1024 * 1024;

    // foreign_keys=ON is included so the raw execution-journal writer's own connections enforce FKs too (EF
    // already sets it on its connections). Without it the writer path could, in principle, orphan cascade
    // children — cheap insurance to set it everywhere the tuning pragmas are applied. journal_size_limit
    // bounds WAL growth (see WalSizeLimitBytes).
    private static readonly string ConnectionPragmaSql =
        $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA journal_size_limit={WalSizeLimitBytes};";

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

    /// <summary>
    /// Ensure the database uses <c>auto_vacuum=INCREMENTAL</c> so deleted rows' pages can be returned to the
    /// OS (via a later <c>PRAGMA incremental_vacuum</c>) instead of leaving the <c>.db</c> file pinned at its
    /// high-water mark forever. auto_vacuum only takes effect on an empty database or after a full
    /// <c>VACUUM</c>, so a database currently in mode NONE (the SQLite default — and every database created
    /// before this was added) is converted here with a one-time VACUUM. A fresh database's VACUUM is trivial;
    /// an existing one pays a single rewrite. Idempotent: a database already in INCREMENTAL mode is left alone.
    /// </summary>
    public static void EnsureIncrementalAutoVacuum(DbConnection connection)
    {
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA auto_vacuum;";
            var mode = Convert.ToInt64(read.ExecuteScalar() ?? 0L);
            if (mode == 2)
            {
                return; // 0=NONE, 1=FULL, 2=INCREMENTAL
            }
        }

        using (var set = connection.CreateCommand())
        {
            set.CommandText = "PRAGMA auto_vacuum=INCREMENTAL;";
            set.ExecuteNonQuery();
        }

        // The mode change only takes hold once the file is rewritten.
        using var vacuum = connection.CreateCommand();
        vacuum.CommandText = "VACUUM;";
        vacuum.ExecuteNonQuery();
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
