using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Infrastructure.Persistence;

public class SqliteExecutionJournalWriter : IExecutionJournalWriter
{
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
            var sql = @"
                INSERT INTO JournalEntries (Id, ExecutionInstanceId, NodeId, Timestamp, EventType, Message, Data)
                VALUES (@Id, @ExecutionInstanceId, @NodeId, @Timestamp, @EventType, @Message, @Data);";

            using var command = new SqliteCommand(sql, connection);

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

            command.Parameters.AddWithValue("@Id", entry.Id);
            command.Parameters.AddWithValue("@ExecutionInstanceId", entry.ExecutionInstanceId.Value);
            command.Parameters.AddWithValue("@NodeId", entry.NodeId.HasValue ? (object)entry.NodeId.Value.Value : DBNull.Value);

            var converter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter();
            var convertedTime = converter.ConvertToProviderExpression.Compile()(entry.Timestamp);
            command.Parameters.AddWithValue("@Timestamp", convertedTime);

            command.Parameters.AddWithValue("@EventType", entry.EventType);
            command.Parameters.AddWithValue("@Message", entry.Message);

            var serializedData = JsonSerializer.Serialize(entry.Data, PersistenceJsonOptions.Default);
            command.Parameters.AddWithValue("@Data", serializedData);

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
}
