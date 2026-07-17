// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Knotarium.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Knotarium.Tests.Api;

/// <summary>
/// Regression guard for the SSRF fix: the outbound notification/alerting HTTP clients
/// (<c>NotificationWebhook</c>, <c>NotificationSlack</c>) post to admin-configured URLs and were
/// previously resolved as unregistered — i.e. default, un-guarded — factory clients. They must now
/// go through the same egress guard as every other outbound client, so a request to a private or
/// loopback address is refused with <see cref="HttpRequestException"/> before any connection is made.
/// </summary>
public sealed class NotificationEgressGuardTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _databasePath;

    public NotificationEgressGuardTests()
    {
        // Isolated SQLite file per host so the executor's single-writer startup guard doesn't abort
        // (a shared DB across parallel test hosts — or a real running instance — would collide).
        _databasePath = Path.Combine(Path.GetTempPath(), $"knotarium-egress-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var db = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (db != null) services.Remove(db);
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
            });
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:Credentials:EncryptionKeyBase64"] = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=",
            }));
        });

        // Force the host to start (and stay started) before we resolve services from it.
        _ = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Theory]
    [InlineData("NotificationWebhook")]
    [InlineData("NotificationSlack")]
    [InlineData("HttpNode")] // the general-purpose client the OAuth token fetch now uses
    public async Task Named_client_blocks_loopback_and_private_targets(string clientName)
    {
        var factory = _factory.Services.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(clientName);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1/hook"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://169.254.169.254/latest/meta-data"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://10.0.0.5/internal"));
    }
}
