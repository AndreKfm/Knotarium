using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Read-side seam over a run's execution instance and journal for slices *outside* Execution (the
/// failure-alert and error-workflow spines) that need to inspect a failed run without binding the
/// concrete <c>AppDbContext</c>. Execution itself still owns the write side directly. The EF adapter
/// lives in Infrastructure.
/// </summary>
public interface IExecutionReadStore
{
    /// <summary>The execution instance with its <see cref="ExecutionInstance.NodeStates"/> loaded, or null if not found.</summary>
    Task<ExecutionInstance?> GetInstanceWithNodeStatesAsync(ExecutionInstanceId executionId, CancellationToken cancellationToken = default);

    /// <summary>The most recent journal entry of the given event type for the run, or null when none exists.</summary>
    Task<ExecutionJournal?> GetLatestJournalEntryAsync(ExecutionInstanceId executionId, string eventType, CancellationToken cancellationToken = default);
}
