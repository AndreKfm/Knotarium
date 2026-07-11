using System;
using System.Collections.Generic;
using System.Linq;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Infrastructure.Persistence;

/// <summary>
/// EF-backed <see cref="IPollingTriggerStore"/> over the shared <see cref="AppDbContext"/>. Owns the
/// due-trigger query (excluding disabled workflows) and the cursor/schedule persistence so the Polling
/// slice never binds the concrete DbContext.
/// </summary>
public sealed class DbPollingTriggerStore : IPollingTriggerStore
{
    private readonly AppDbContext _dbContext;

    public DbPollingTriggerStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<PollingTrigger>> GetDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var disabledWorkflowIds = _dbContext.WorkflowDefinitions
            .Where(workflow => !workflow.IsEnabled)
            .Select(workflow => workflow.Id);

        return await _dbContext.PollingTriggers
            .Where(trigger => trigger.IsActive
                && trigger.NextPollAtUtc <= now
                && !disabledWorkflowIds.Contains(trigger.WorkflowDefinitionId))
            .OrderBy(trigger => trigger.NextPollAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task SaveAsync(PollingTrigger trigger, CancellationToken cancellationToken = default)
    {
        _dbContext.PollingTriggers.Update(trigger);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
