namespace KnotGarden.Core.Contracts;

/// <summary>
/// Read-only view of the global runtime "armed" switch (design-time vs run-time). Feature code that only
/// needs to observe whether automatic triggers may fire — e.g. backup/restore refusing to run while armed
/// — depends on this seam instead of the host-owned concrete arming state.
/// </summary>
public interface IRuntimeArmingState
{
    /// <summary>Whether automatic schedule/trigger evaluation is currently armed.</summary>
    bool IsArmed { get; }
}
