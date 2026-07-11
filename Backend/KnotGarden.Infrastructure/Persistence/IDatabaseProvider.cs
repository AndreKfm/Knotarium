using Microsoft.EntityFrameworkCore;

namespace KnotGarden.Infrastructure.Persistence;

public interface IDatabaseProvider
{
    string Name { get; }
    void Configure(DbContextOptionsBuilder builder, string connectionString);
}
