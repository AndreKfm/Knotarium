// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Knotarium.Tests.Nodes;

public class DbQueryNodeTaskTests : IAsyncLifetime
{
    // A shared-cache in-memory SQLite DB kept alive by one open connection for the test's lifetime;
    // the node opens its own connection to the same string and sees the same tables. The DB name is
    // unique per test instance so the tests never share state.
    private readonly string _connectionString = $"Data Source=file:dbq_{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _keepAlive;

    public DbQueryNodeTaskTests() => _keepAlive = new SqliteConnection(_connectionString);

    public async Task InitializeAsync()
    {
        await _keepAlive.OpenAsync();
        await using var command = _keepAlive.CreateCommand();
        command.CommandText = "CREATE TABLE t(id INTEGER, name TEXT); INSERT INTO t VALUES (1,'alice'),(2,'bob');";
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private sealed class StubCapabilityPolicy : ICapabilityPolicy
    {
        private readonly bool _enabled;
        public StubCapabilityPolicy(bool enabled) => _enabled = enabled;
        public Task<bool> IsEnabledAsync(string capability, CancellationToken cancellationToken = default) => Task.FromResult(_enabled);
    }

    private DbQueryNodeTask NewTask(bool dbEnabled = true) =>
        new(new FakeSecretResolver(new Dictionary<string, string> { ["conn"] = _connectionString }), new StubCapabilityPolicy(dbEnabled));

    private static NodeExecutionContext Context(Dictionary<string, object> inputs) => new(
        WorkflowId: WorkflowDefinitionId.New(),
        ExecutionId: Guid.NewGuid(),
        NodeId: NodeId.Create("db-1"),
        Inputs: inputs,
        GlobalVariables: new Dictionary<string, object>());

    private static (List<Dictionary<string, object?>> Rows, long RowCount) ReadResult(LegacyNodeResult result)
    {
        var success = Assert.IsType<LegacyNodeResult.Success>(result);
        var payload = (Dictionary<string, object?>)success.Outputs!["result"];
        var rows = (List<Dictionary<string, object?>>)payload["rows"]!;
        return (rows, Convert.ToInt64(payload["rowCount"]));
    }

    [Fact]
    public async Task When_database_capability_disabled_returns_failure_without_querying()
    {
        var result = await NewTask(dbEnabled: false).ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["provider"] = "sqlite",
            ["connectionRef"] = "conn",
            ["query"] = "SELECT * FROM t",
        }), CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("capability", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Select_with_bound_parameter_returns_matching_rows()
    {
        var result = await NewTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["provider"] = "sqlite",
            ["connectionRef"] = "conn",
            ["query"] = "SELECT id, name FROM t WHERE name = @who ORDER BY id",
            ["parameters"] = new List<Dictionary<string, object>> { new() { ["name"] = "who", ["value"] = "alice" } },
        }), CancellationToken.None);

        var (rows, _) = ReadResult(result);
        Assert.Single(rows);
        Assert.Equal(1L, Convert.ToInt64(rows[0]["id"]));
        Assert.Equal("alice", rows[0]["name"]);
    }

    [Fact]
    public async Task Insert_reports_row_count_and_persists()
    {
        var result = await NewTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["provider"] = "sqlite",
            ["connectionRef"] = "conn",
            ["query"] = "INSERT INTO t (id, name) VALUES (@id, @name)",
            ["parameters"] = new List<Dictionary<string, object>>
            {
                new() { ["name"] = "id", ["value"] = 3 },
                new() { ["name"] = "name", ["value"] = "carol" },
            },
        }), CancellationToken.None);

        var (_, rowCount) = ReadResult(result);
        Assert.Equal(1L, rowCount);

        await using var verify = _keepAlive.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM t";
        Assert.Equal(3L, (long)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Parameter_value_is_bound_not_interpolated_so_injection_is_inert()
    {
        var result = await NewTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["provider"] = "sqlite",
            ["connectionRef"] = "conn",
            ["query"] = "SELECT * FROM t WHERE name = @who",
            ["parameters"] = new List<Dictionary<string, object>>
            {
                new() { ["name"] = "who", ["value"] = "alice'); DROP TABLE t; --" },
            },
        }), CancellationToken.None);

        var (rows, _) = ReadResult(result);
        Assert.Empty(rows); // no match — and, crucially, the table survives:
        await using var verify = _keepAlive.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM t";
        Assert.Equal(2L, (long)(await verify.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Missing_query_fails()
    {
        var result = await NewTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["provider"] = "sqlite",
            ["connectionRef"] = "conn",
        }), CancellationToken.None);
        Assert.IsType<LegacyNodeResult.Failure>(result);
    }

    [Fact]
    public async Task Unresolved_connection_fails()
    {
        var result = await NewTask().ExecuteAsync(Context(new Dictionary<string, object>
        {
            ["provider"] = "sqlite",
            ["connectionRef"] = "does-not-exist",
            ["query"] = "SELECT 1",
        }), CancellationToken.None);
        Assert.IsType<LegacyNodeResult.Failure>(result);
    }

    private sealed class FakeSecretResolver : ISecretResolver
    {
        private readonly Dictionary<string, string> _secrets;
        public FakeSecretResolver(Dictionary<string, string> secrets) => _secrets = secrets;
        public Task<string?> ResolveAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.FromResult(_secrets.TryGetValue(secretRef, out var value) ? value : null);
    }
}
