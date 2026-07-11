using System;

namespace KnotGarden.Core.Domain;

public enum ScheduleFireStatus
{
    Claimed = 0,
    ExecutionCreated = 1,
    Failed = 2
}

public sealed class ScheduleFire
{
    public Guid Id { get; set; }
    public Guid ScheduleId { get; set; }
    public DateTimeOffset PlannedFireAtUtc { get; set; }
    public DateTimeOffset FiredAtUtc { get; set; }
    public ExecutionInstanceId? ExecutionInstanceId { get; set; }
    public ScheduleFireStatus Status { get; set; }
}
