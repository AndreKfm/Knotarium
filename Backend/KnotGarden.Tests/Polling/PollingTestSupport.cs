using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using KnotGarden.Infrastructure.Persistence;

namespace KnotGarden.Tests.Polling;

/// <summary>A TimeProvider whose "now" can be set explicitly in tests.</summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public FixedTimeProvider(DateTimeOffset now) => _now = now;
    public void Set(DateTimeOffset now) => _now = now;
    public void Advance(TimeSpan by) => _now += by;
    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>Creates an isolated SQLite-backed AppDbContext for a single test.</summary>
public static class PollingTestDb
{
    public static (SqliteConnection connection, DbContextOptions<AppDbContext> options) NewOptions()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        using var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        return (connection, options);
    }
}
