using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace KnotGarden.Features.Nodes;

/// <summary>
/// Runs a parameterized SQL query against a relational database (Postgres or SQLite). The connection
/// string is resolved from a stored credential (never persisted in the workflow), and every query
/// parameter is bound via <see cref="DbParameter"/> — never string-concatenated — so the node is
/// SQL-injection safe. Emits <c>result = { rows, rowCount }</c>: SELECTs fill <c>rows</c>; writes
/// (INSERT/UPDATE/DELETE) report <c>rowCount</c> with an empty <c>rows</c>.
/// </summary>
public class DbQueryNodeTask : INodeTask
{
    private readonly ISecretResolver _secretResolver;
    private readonly ICapabilityPolicy _capabilities;

    public DbQueryNodeTask(ISecretResolver secretResolver, ICapabilityPolicy capabilities)
    {
        _secretResolver = secretResolver;
        _capabilities = capabilities;
    }

    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        // Database access is a privileged capability, off unless an admin enables it.
        if (!await _capabilities.IsEnabledAsync(KnotGarden.Core.Domain.NodeCapabilities.Database, cancellationToken))
        {
            return new LegacyNodeResult.Failure(
                "Database Query is disabled: the 'database' capability is off. An administrator can enable it under Settings → Capabilities.");
        }

        var provider = (Input(context, "provider") ?? string.Empty).Trim().ToLowerInvariant();
        var query = Input(context, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return new LegacyNodeResult.Failure("Database query failed: missing required 'query'.");
        }

        var connectionRef = Input(context, "connectionRef");
        var connectionString = !string.IsNullOrWhiteSpace(connectionRef)
            ? await _secretResolver.ResolveAsync(connectionRef!, cancellationToken)
            : null;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new LegacyNodeResult.Failure("Database query failed: no connection string resolved from 'connectionRef'.");
        }

        DbConnection connection;
        try
        {
            connection = provider switch
            {
                "postgres" or "postgresql" => new NpgsqlConnection(connectionString),
                "sqlite" => new SqliteConnection(connectionString),
                _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use 'postgres' or 'sqlite'."),
            };
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"Database query failed: {ex.Message}");
        }

        try
        {
            await using (connection)
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = query;
                AddParameters(command, context);

                var rows = new List<Dictionary<string, object?>>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                do
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var row = new Dictionary<string, object?>(reader.FieldCount);
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            row[reader.GetName(i)] = Normalize(reader.GetValue(i));
                        }
                        rows.Add(row);
                    }
                }
                while (await reader.NextResultAsync(cancellationToken));

                var result = new Dictionary<string, object?>
                {
                    ["rows"] = rows,
                    ["rowCount"] = reader.RecordsAffected >= 0 ? reader.RecordsAffected : rows.Count,
                };
                return new LegacyNodeResult.Success(new Dictionary<string, object> { ["result"] = result });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"Database query failed: {ex.Message}");
        }
    }

    private static string? Input(NodeExecutionContext context, string key)
        => context.Inputs.TryGetValue(key, out var value) ? value?.ToString() : null;

    /// <summary>
    /// Binds the node's <c>parameters</c> (a keyValue map) as named <see cref="DbParameter"/>s. The key
    /// may be written with or without a leading <c>@</c>/<c>:</c>/<c>$</c>; it is bound as <c>@name</c>,
    /// which both Npgsql and Microsoft.Data.Sqlite accept. Values are never interpolated into the SQL.
    /// </summary>
    private static void AddParameters(DbCommand command, NodeExecutionContext context)
    {
        if (!context.Inputs.TryGetValue("parameters", out var raw) || raw is null)
        {
            return;
        }

        foreach (var (name, value) in EnumerateParameters(raw))
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@" + name.TrimStart('@', ':', '$');
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }

    /// <summary>Reads the keyValue <c>parameters</c> value across the shapes it can arrive in: a JSON
    /// array of {name,value}, a JSON object, a list of dictionaries, or a plain dictionary.</summary>
    private static IEnumerable<(string Name, object? Value)> EnumerateParameters(object raw)
    {
        if (raw is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    {
                        item.TryGetProperty("value", out var v);
                        yield return (n.GetString()!, FromJson(v));
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    yield return (property.Name, FromJson(property.Value));
                }
            }
            yield break;
        }

        if (raw is IDictionary<string, object> dictionary)
        {
            foreach (var pair in dictionary)
            {
                yield return (pair.Key, pair.Value);
            }
            yield break;
        }

        if (raw is IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                if (item is IDictionary<string, object> row
                    && row.TryGetValue("name", out var name) && name is string nameString)
                {
                    row.TryGetValue("value", out var value);
                    yield return (nameString, value);
                }
            }
        }
    }

    private static object? FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };

    /// <summary>Coerces a DB value into a JSON-friendly shape for the outputs map.</summary>
    private static object? Normalize(object value) => value switch
    {
        DBNull => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        Guid guid => guid.ToString(),
        _ => value,
    };
}
