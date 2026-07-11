using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Domain;

namespace KnotGarden.Core.Contracts;

/// <summary>
/// Read/persist seam over schedules for the schedule evaluation loop, so it can find due schedules and
/// advance a schedule's next-fire time without binding the concrete <c>AppDbContext</c>. The
/// transactional fire-claim (schedule fire + execution instance) lives behind
/// <see cref="IWorkflowEnqueueService"/>. The EF adapter lives in Infrastructure.
/// </summary>
public interface IScheduleStore
{
    /// <summary>
    /// Active schedules due at or before <paramref name="now"/> whose owning workflow is enabled,
    /// ordered earliest-first.
    /// </summary>
    Task<IReadOnlyList<Schedule>> GetDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Advances a schedule's next-fire time (used when a duplicate fire claim is rejected).</summary>
    Task AdvanceNextFireAsync(Schedule schedule, DateTimeOffset nextFireAtUtc, CancellationToken cancellationToken = default);
}
