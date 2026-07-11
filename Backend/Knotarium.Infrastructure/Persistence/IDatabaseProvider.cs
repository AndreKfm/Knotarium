using Microsoft.EntityFrameworkCore;

namespace Knotarium.Infrastructure.Persistence;

public interface IDatabaseProvider
{
    string Name { get; }
    void Configure(DbContextOptionsBuilder builder, string connectionString);
}
