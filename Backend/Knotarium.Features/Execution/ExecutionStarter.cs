using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Features.Execution;

/// <summary>
/// The shared tail of every "start a run now" path — manual trigger, manual schedule fire, and the
/// external <c>/api/executions</c> webhook: compile the runtime workflow, persist a Pending
/// <see cref="ExecutionInstance"/>, and queue it for the background worker.
/// </summary>
/// <remarks>
/// Callers keep their own pre-flight checks (existence, enablement, active-version, node validation) and
/// supply the trigger-origin label plus any seed variables; this collapses the compile+persist+enqueue
/// body that was copied verbatim across the three endpoints.
/// </remarks>
public sealed class ExecutionStarter(
    AppDbContext dbContext,
    WorkflowCompiler compiler,
    WorkflowExecutionQueue queue)
{
    /// <summary>
    /// Compiles <paramref name="runtimeWorkflow"/> and, on success, persists and queues a Pending run
    /// for the given active <paramref name="versionId"/>.
    /// </summary>
    public async Task<ExecutionStartOutcome> StartAsync(
        WorkflowDefinition runtimeWorkflow,
        WorkflowVersionId versionId,
        string triggerOrigin,
        Dictionary<string, object>? globalVariables = null,
        CancellationToken cancellationToken = default)
    {
        var compilation = await compiler.CompileAsync(runtimeWorkflow, cancellationToken).ConfigureAwait(false);
        if (!compilation.IsSuccess || compilation.Plan is null)
        {
            return ExecutionStartOutcome.CompilationFailed(compilation.Diagnostics);
        }

        var instance = new ExecutionInstance
        {
            Id = ExecutionInstanceId.New(),
            WorkflowDefinitionId = runtimeWorkflow.Id,
            WorkflowVersionId = versionId,
            Status = ExecutionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TriggerOrigin = triggerOrigin,
            GlobalVariables = globalVariables ?? new Dictionary<string, object>(),
        };

        dbContext.ExecutionInstances.Add(instance);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        queue.QueueExecution(instance.Id);
        return ExecutionStartOutcome.Started(instance);
    }
}

/// <summary>
/// Result of <see cref="ExecutionStarter.StartAsync"/>: either a queued <see cref="ExecutionInstance"/>
/// (<see cref="IsStarted"/> is <see langword="true"/>) or the compilation diagnostics that blocked it.
/// </summary>
public sealed record ExecutionStartOutcome(
    ExecutionInstance? Instance,
    ImmutableArray<CompilationDiagnostic> Diagnostics)
{
    /// <summary>Whether a run was compiled, persisted and queued.</summary>
    public bool IsStarted => Instance is not null;

    internal static ExecutionStartOutcome Started(ExecutionInstance instance) =>
        new(instance, ImmutableArray<CompilationDiagnostic>.Empty);

    internal static ExecutionStartOutcome CompilationFailed(ImmutableArray<CompilationDiagnostic> diagnostics) =>
        new(null, diagnostics);
}
