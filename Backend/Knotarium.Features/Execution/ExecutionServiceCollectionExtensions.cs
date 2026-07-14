using Knotarium.Core.Contracts;
using Knotarium.Features.Execution;

// .NET convention: DI registration extensions live in Microsoft.Extensions.DependencyInjection
// so callers get AddExecution() without an extra using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the execution slice: the workflow executor and its supporting run/recovery/replay
/// services, the singleton execution queue and its hosted worker, and the enqueuers that feed runs in
/// from external signals and from the error-workflow spine.
/// </summary>
/// <remarks>
/// Lifetime coupling is load-bearing: <see cref="WorkflowExecutionQueue"/> is a singleton written from
/// the scoped <see cref="WorkflowExecutor"/> and drained by the hosted <see cref="WorkflowExecutionWorker"/>,
/// so all three must keep these exact scopes. The runtime arming switch, the scheduling/polling hosted
/// workers and the external-signal trigger registry are host-lifecycle concerns and stay in the host.
/// </remarks>
public static class ExecutionServiceCollectionExtensions
{
    public static IServiceCollection AddExecution(this IServiceCollection services)
    {
        // Clamped run-level knobs (concurrency, queue cap, journal batching), bound from the host's
        // configuration at first resolve so AddExecution keeps its parameterless call shape.
        services.AddSingleton(sp => ExecutionOptions.FromConfiguration(
            sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>()));
        services.AddSingleton<ExecutionRuntimeMonitor>();
        services.AddSingleton(sp =>
        {
            var queue = new WorkflowExecutionQueue(sp.GetRequiredService<ExecutionOptions>());
            // Late-bind the metrics gauge to the live queue (telemetry is registered by the host's
            // persistence wiring; absent in slim test containers).
            sp.GetService<ExecutionTelemetry>()?.RegisterQueueDepthProvider(() => queue.Depth);
            return queue;
        });
        // Alias the producer seam to the same singleton so run-starting slices (Polling, Schedules)
        // enqueue through IWorkflowExecutionQueue without depending on the Execution slice.
        services.AddSingleton<IWorkflowExecutionQueue>(sp => sp.GetRequiredService<WorkflowExecutionQueue>());
        services.AddHostedService<WorkflowExecutionWorker>();
        services.AddScoped<WorkflowExecutor>();
        services.AddScoped<RecoveryService>();
        services.AddScoped<ReplayService>();

        // Shared compile+persist+enqueue tail for the manual-trigger / schedule-fire / webhook endpoints.
        services.AddScoped<ExecutionStarter>();

        // Enqueuers: external-signal (Event/Action triggers) and error-workflow runs both re-enter
        // execution through the queue.
        services.AddScoped<IExternalSignalRunEnqueuer, ExternalSignalRunEnqueuer>();
        services.AddScoped<IPollRunEnqueuer, PollRunEnqueuer>();  // run creation for the Polling slice's Core seam
        services.AddScoped<IWorkflowEnqueueService, WorkflowEnqueueService>();  // transactional schedule-fire claim + run creation for the Schedules slice's Core seam
        services.AddScoped<ErrorWorkflowRunEnqueuer>();
        // Alias the seam to the same scoped instance so the Notifications error-workflow worker can
        // enqueue an error run without depending on the Execution slice.
        services.AddScoped<IErrorWorkflowRunEnqueuer>(sp => sp.GetRequiredService<ErrorWorkflowRunEnqueuer>());
        return services;
    }
}
