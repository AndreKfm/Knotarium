using Microsoft.EntityFrameworkCore;

namespace Knotarium.Infrastructure.Persistence;

public class SqliteDatabaseProvider : IDatabaseProvider
{
    public string Name => "SQLite";

    public void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        builder.UseSqlite(connectionString);
    }
}
