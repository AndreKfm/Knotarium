// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Infrastructure.Persistence;

public class SqliteExecutionJournalWriter : IExecutionJournalWriter
{
    private const string InsertSql = @"
        INSERT INTO JournalEntries (Id, ExecutionInstanceId, NodeId, Timestamp, EventType, Message, Data)
        VALUES (@Id, @ExecutionInstanceId, @NodeId, @Timestamp, @EventType, @Message, @Data);";

    // Same on-disk representation EF uses for DateTimeOffset, hoisted so the expression is compiled once.
    private static readonly Func<DateTimeOffset, long> ToProviderTimestamp =
        new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter()
            .ConvertToProviderExpression.Compile();

    private readonly string _connectionString;
    private readonly SqliteConnection? _testConnection;

    public SqliteExecutionJournalWriter(IConfiguration configuration)
    {
        _connectionString = configuration["Database:ConnectionString"] ?? "Data Source=Knotarium.db";
    }

    public SqliteExecutionJournalWriter(string connectionString, SqliteConnection? testConnection = null)
    {
        _connectionString = connectionString;
        _testConnection = testConnection;
    }

    public async Task WriteAsync(ExecutionJournal entry)
    {
        SqliteConnection connection;
        bool shouldDispose = false;

        if (_testConnection != null)
        {
            connection = _testConnection;
        }
        else
        {
            connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            // Pooling reuses the underlying handle, so this is a cheap idempotent no-op after the first open;
            // it guarantees synchronous=NORMAL (no fsync per journal row) on the writer's own connections.
            await SqlitePragmas.ApplyConnectionPragmasAsync(connection);
            shouldDispose = true;
        }

        try
        {
            using var command = new SqliteCommand(InsertSql, connection);

            // Reflection-based transaction enlistment to support shared connections in tests
            var txField = connection.GetType().GetField("_transaction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? connection.GetType().GetField("Transaction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (txField != null)
            {
                command.Transaction = txField.GetValue(connection) as SqliteTransaction;
            }
            else
            {
                // Fallback: search all fields for a SqliteTransaction
                var fields = connection.GetType().GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                foreach (var field in fields)
                {
                    if (typeof(SqliteTransaction).IsAssignableFrom(field.FieldType))
                    {
                        var tx = field.GetValue(connection) as SqliteTransaction;
                        if (tx != null)
                        {
                            command.Transaction = tx;
                            break;
                        }
                    }
                }
            }

            AddEntryParameters(command);
            BindEntry(command, entry);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            if (shouldDispose)
            {
                await connection.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Writes all entries inside ONE transaction on one connection — a single write-lock acquisition and
    /// a single commit for the whole batch, instead of one per row. This is the write-side lever that
    /// keeps SQLite contention flat under concurrent runs (the journal is the highest-volume table).
    /// </summary>
    public async Task WriteBatchAsync(IReadOnlyList<ExecutionJournal> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        if (entries.Count == 1 || _testConnection != null)
        {
            // Single row (no batching win), or the shared test connection (whose ambient transaction the
            // per-row path already enlists in): the row-by-row path is the safe one.
            foreach (var entry in entries)
            {
                await WriteAsync(entry);
            }
            return;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await SqlitePragmas.ApplyConnectionPragmasAsync(connection);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using var command = new SqliteCommand(InsertSql, connection, transaction);
        AddEntryParameters(command);

        foreach (var entry in entries)
        {
            BindEntry(command, entry);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static void AddEntryParameters(SqliteCommand command)
    {
        command.Parameters.Add("@Id", SqliteType.Text);
        command.Parameters.Add("@ExecutionInstanceId", SqliteType.Text);
        command.Parameters.Add("@NodeId", SqliteType.Text);
        command.Parameters.Add("@Timestamp", SqliteType.Integer);
        command.Parameters.Add("@EventType", SqliteType.Text);
        command.Parameters.Add("@Message", SqliteType.Text);
        command.Parameters.Add("@Data", SqliteType.Text);
    }

    private static void BindEntry(SqliteCommand command, ExecutionJournal entry)
    {
        command.Parameters["@Id"].Value = entry.Id;
        command.Parameters["@ExecutionInstanceId"].Value = entry.ExecutionInstanceId.Value;
        command.Parameters["@NodeId"].Value = entry.NodeId.HasValue ? (object)entry.NodeId.Value.Value : DBNull.Value;
        command.Parameters["@Timestamp"].Value = ToProviderTimestamp(entry.Timestamp);
        command.Parameters["@EventType"].Value = entry.EventType;
        command.Parameters["@Message"].Value = entry.Message;
        command.Parameters["@Data"].Value = JsonSerializer.Serialize(entry.Data, PersistenceJsonOptions.Default);
    }
}
