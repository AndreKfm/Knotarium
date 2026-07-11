using System.Linq;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Infrastructure.Persistence;

/// <summary>
/// EF-backed <see cref="IExecutionReadStore"/> over the shared <see cref="AppDbContext"/>. Owns the
/// failure-path reads of the <c>ExecutionInstances</c> and <c>JournalEntries</c> tables so the
/// Notifications slice never binds the concrete DbContext.
/// </summary>
public sealed class DbExecutionReadStore : IExecutionReadStore
{
    private readonly AppDbContext _dbContext;

    public DbExecutionReadStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ExecutionInstance?> GetInstanceWithNodeStatesAsync(ExecutionInstanceId executionId, CancellationToken cancellationToken = default)
        => await _dbContext.ExecutionInstances
            .Include(e => e.NodeStates)
            .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken);

    public async Task<ExecutionJournal?> GetLatestJournalEntryAsync(ExecutionInstanceId executionId, string eventType, CancellationToken cancellationToken = default)
        => await _dbContext.JournalEntries
            .Where(j => j.ExecutionInstanceId == executionId && j.EventType == eventType)
            .OrderByDescending(j => j.Timestamp)
            .FirstOrDefaultAsync(cancellationToken);
}
