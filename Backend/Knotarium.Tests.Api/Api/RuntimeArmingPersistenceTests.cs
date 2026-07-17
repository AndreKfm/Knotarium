// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Knotarium.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Knotarium.Tests.Api;

/// <summary>
/// The runtime arming switch must survive a process restart: the arming endpoint persists the
/// operator's explicit choice to the AppSettings table, and startup restores it with precedence
/// persisted value > "Runtime:Armed" config seed > disarmed. Restart is simulated by standing up
/// a second host over the same SQLite file.
/// </summary>
public sealed class RuntimeArmingPersistenceTests : IDisposable
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public RuntimeArmingPersistenceTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"knotarium-arming-{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private WebApplicationFactory<Program> CreateHost(bool? configArmed = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Arming endpoints sit behind the secure-by-default fallback policy; these tests cover
            // persistence semantics, not the auth gate (AuthEndpointTests covers that).
            builder.UseSetting("Auth:Enabled", "false");

            if (configArmed is { } armed)
            {
                builder.UseSetting("Runtime:Armed", armed ? "true" : "false");
            }

            builder.ConfigureServices(services =>
            {
                var db = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (db != null) services.Remove(db);
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connectionString));
            });
        });

    /// <summary>
    /// The execution worker's startup guard refuses to start while another worker's heartbeat is
    /// younger than 10s — which is exactly what a back-to-back restart in a test looks like. Age the
    /// previous host's heartbeat (as a real restart's stop/start gap would) so the new host comes up.
    /// </summary>
    private void ExpireWorkerHeartbeats()
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ActiveWorkers SET LastHeartbeat = 0;";
        command.ExecuteNonQuery();
    }

    private static async Task<bool> GetArmedAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/runtime/arming");
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("armed").GetBoolean();
    }

    [Fact]
    public async Task Fresh_install_starts_disarmed()
    {
        using var factory = CreateHost();
        using var client = factory.CreateClient();

        Assert.False(await GetArmedAsync(client));
    }

    [Fact]
    public async Task Armed_state_survives_a_restart()
    {
        using (var factory = CreateHost())
        using (var client = factory.CreateClient())
        {
            (await client.PostAsJsonAsync("/api/runtime/arming", new { armed = true })).EnsureSuccessStatusCode();
            Assert.True(await GetArmedAsync(client));
        }

        ExpireWorkerHeartbeats();

        // "Restart": a second host over the same database file must come up armed.
        using (var restarted = CreateHost())
        using (var client = restarted.CreateClient())
        {
            Assert.True(await GetArmedAsync(client));
        }
    }

    [Fact]
    public async Task Persisted_disarm_wins_over_an_armed_config_seed()
    {
        using (var factory = CreateHost())
        using (var client = factory.CreateClient())
        {
            (await client.PostAsJsonAsync("/api/runtime/arming", new { armed = false })).EnsureSuccessStatusCode();
        }

        ExpireWorkerHeartbeats();

        // The operator explicitly disarmed; a "Runtime:Armed=true" config seed must not override that.
        using (var restarted = CreateHost(configArmed: true))
        using (var client = restarted.CreateClient())
        {
            Assert.False(await GetArmedAsync(client));
        }
    }

    [Fact]
    public async Task Config_seed_applies_while_nothing_is_persisted()
    {
        using var factory = CreateHost(configArmed: true);
        using var client = factory.CreateClient();

        Assert.True(await GetArmedAsync(client));
    }
}
