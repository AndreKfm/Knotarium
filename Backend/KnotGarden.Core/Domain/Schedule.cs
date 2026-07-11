using System;

namespace KnotGarden.Core.Domain;

public sealed class Schedule
{
    public Guid Id { get; set; }
    public WorkflowDefinitionId WorkflowDefinitionId { get; set; }
    public string CronExpression { get; set; } = null!;
    public string TimeZoneId { get; set; } = null!; // Local semantic time tracking (e.g. Europe/Berlin)
    public DateTimeOffset NextFireAtUtc { get; set; } // Tracked and evaluated in UTC
    public bool IsActive { get; set; }
}
