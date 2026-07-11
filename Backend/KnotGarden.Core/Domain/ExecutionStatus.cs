namespace KnotGarden.Core.Domain;

public enum ExecutionStatus
{
    Pending = 0,
    Running = 1,
    Suspended = 2,
    Cancelled = 3,
    Completed = 4,
    Failed = 5,
    WaitingForRetry = 6,
    /// <summary>A failed run triaged away in the dead-letter view; no longer surfaced as actionable.</summary>
    Discarded = 7
}
