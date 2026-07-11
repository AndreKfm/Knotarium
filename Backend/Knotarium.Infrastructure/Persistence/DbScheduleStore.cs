using System;
using System.Collections.Generic;
using System.Linq;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Infrastructure.Persistence;

/// <summary>
/// EF-backed <see cref="IScheduleStore"/> over the shared <see cref="AppDbContext"/>. Owns the
/// due-schedule query (excluding disabled workflows) and the targeted next-fire advance so the
/// Schedules slice's evaluation loop never binds the concrete DbContext.
/// </summary>
public sealed class DbScheduleStore : IScheduleStore
{
    private readonly AppDbContext _dbContext;

    public DbScheduleStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<Schedule>> GetDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var disabledWorkflowIds = _dbContext.WorkflowDefinitions
            .Where(workflow => !workflow.IsEnabled)
            .Select(workflow => workflow.Id);

        return await _dbContext.Schedules
            .Where(schedule => schedule.IsActive
                && schedule.NextFireAtUtc <= now
                && !disabledWorkflowIds.Contains(schedule.WorkflowDefinitionId))
            .OrderBy(schedule => schedule.NextFireAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task AdvanceNextFireAsync(Schedule schedule, DateTimeOffset nextFireAtUtc, CancellationToken cancellationToken = default)
    {
        schedule.NextFireAtUtc = nextFireAtUtc;
        // Targeted update of just the next-fire column (matches the original evaluation-loop write).
        _dbContext.Entry(schedule).Property(item => item.NextFireAtUtc).IsModified = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
