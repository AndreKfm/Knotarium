using KnotGarden.Core.Domain;

namespace KnotGarden.Features.Execution;

/// <summary>
/// The rule governing dead-letter discard: only a <see cref="ExecutionStatus.Failed"/> run may be
/// discarded. Running/pending/completed/already-discarded runs are not eligible.
/// </summary>
public static class ExecutionDiscardPolicy
{
    public static bool CanDiscard(ExecutionStatus status) => status == ExecutionStatus.Failed;
}
