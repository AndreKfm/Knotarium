using System.Threading;
using System.Threading.Tasks;

namespace KnotGarden.Core.Contracts;

/// <summary>
/// Evaluates active schedules that are due to fire and advances them through the enqueue boundary.
/// </summary>
public interface IScheduleEvaluationService
{
    /// <summary>
    /// Evaluates active schedules that are due as of the current polling instant.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the current evaluation cycle finishes.</returns>
    Task EvaluateActiveSchedulesAsync(CancellationToken cancellationToken = default);
}