using Knotarium.Core.Contracts;
using Knotarium.Features.Schedules;

// .NET convention: DI registration extensions live in Microsoft.Extensions.DependencyInjection
// so callers get AddSchedules() without an extra using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the schedule slice: the cron/interval evaluation service and the enqueue service the
/// scheduling worker drives. The hosted <c>SchedulingWorker</c> and the schedule synchronizer stay
/// in the host (they are host-lifecycle concerns, not slice-owned).
/// </summary>
public static class SchedulesServiceCollectionExtensions
{
    public static IServiceCollection AddSchedules(this IServiceCollection services)
    {
        // IWorkflowEnqueueService's implementation now lives in the Execution slice (the ScheduleFire
        // claim + ExecutionInstance creation are one transaction and belong with run creation); it's
        // registered in AddExecution and consumed here via the Core seam.
        services.AddScoped<IScheduleEvaluationService, ScheduleEvaluationService>();
        return services;
    }
}
