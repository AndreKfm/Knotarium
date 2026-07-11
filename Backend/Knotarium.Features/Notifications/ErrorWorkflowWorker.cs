using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Settings;

namespace Knotarium.Features.Notifications;

/// <summary>
/// Drains <see cref="ErrorWorkflowQueue"/> and, when a global default error workflow is configured,
/// starts a run of it carrying the failed run's context. Mirrors <see cref="FailureAlertWorker"/>:
/// fully try/catch so a dispatch failure is logged but never crashes the loop or a workflow run.
/// </summary>
public class ErrorWorkflowWorker : BackgroundService
{
    private readonly ErrorWorkflowQueue _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ErrorWorkflowWorker> _logger;

    public ErrorWorkflowWorker(
        ErrorWorkflowQueue queue,
        IServiceProvider serviceProvider,
        ILogger<ErrorWorkflowWorker> logger)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Error Workflow Worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            ExecutionInstanceId executionId;
            try
            {
                executionId = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await DispatchAsync(executionId, stoppingToken);
            }
            catch (Exception ex)
            {
                // Last-resort guard: error-workflow dispatch must never crash the worker loop.
                _logger.LogError(ex, "Failed to dispatch error workflow for execution {ExecutionId}.", executionId);
            }
        }

        _logger.LogInformation("Error Workflow Worker stopped.");
    }

    /// <summary>
    /// Loop-prevention decision (both guards required). Returns false — i.e. do NOT start the error
    /// workflow — when the failed run IS the error workflow, or when the failed run was itself started
    /// as an error handler (origin "error"). Pure so the invariant is directly testable.
    /// </summary>
    public static bool ShouldStartErrorWorkflow(string failedWorkflowId, string triggerOrigin, string defaultErrorWorkflowId)
    {
        if (string.Equals(failedWorkflowId, defaultErrorWorkflowId, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(triggerOrigin, "error", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// The failure-context payload emitted on the error workflow's <c>errorTrigger.result</c> port.
    /// Pure so the shape is testable and stable; downstream nodes branch on these fields
    /// (e.g. switch on <c>failedNodeType</c> or match <c>errorMessage</c>).
    /// </summary>
    public static Dictionary<string, object?> BuildPayload(FailureAlertMessage context, string? failedNodeType) => new()
    {
        ["workflowId"] = context.WorkflowId,
        ["workflowName"] = context.WorkflowName,
        ["executionId"] = context.ExecutionId,
        ["failedNodeId"] = context.FailedNodeId,
        ["failedNodeType"] = failedNodeType,
        ["errorMessage"] = context.ErrorMessage,
        ["triggerOrigin"] = context.TriggerOrigin,
        ["timestampUtc"] = context.TimestampUtc,
    };

    /// <summary>
    /// The failure context flattened into individual global variables, so an error workflow can use
    /// each field directly — e.g. a Log node message <c>"{errorMessage} in {errorFailedNodeType}"</c>
    /// (the Log node substitutes <c>{key}</c> from globals) or an Inline Code node via
    /// <c>context.State.GetVariable&lt;string&gt;("errorMessage")</c>. The bundled object is still on
    /// the <c>errorTrigger.result</c> port for nodes that want the whole payload.
    /// </summary>
    public static Dictionary<string, object?> BuildGlobals(FailureAlertMessage context, string? failedNodeType) => new()
    {
        ["errorWorkflowId"] = context.WorkflowId,
        ["errorWorkflowName"] = context.WorkflowName,
        ["errorExecutionId"] = context.ExecutionId,
        ["errorFailedNodeId"] = context.FailedNodeId,
        ["errorFailedNodeType"] = failedNodeType,
        ["errorMessage"] = context.ErrorMessage,
        ["errorTriggerOrigin"] = context.TriggerOrigin,
        ["errorTimestampUtc"] = context.TimestampUtc,
    };

    /// <summary>Looks up the node type of the failed node from the failed workflow's definition.</summary>
    public static string? ResolveFailedNodeType(WorkflowDefinition? workflow, string? failedNodeId)
    {
        if (workflow is null || string.IsNullOrEmpty(failedNodeId))
        {
            return null;
        }

        foreach (var node in workflow.Nodes)
        {
            if (string.Equals(node.Id.Value, failedNodeId, StringComparison.Ordinal))
            {
                return node.Type;
            }
        }

        return null;
    }

    private async Task DispatchAsync(ExecutionInstanceId executionId, CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var readStore = scope.ServiceProvider.GetRequiredService<IExecutionReadStore>();
        var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();

        var defaultErrorWorkflowId = await settings.GetDefaultErrorWorkflowIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(defaultErrorWorkflowId))
        {
            return; // No global error workflow configured — nothing to do.
        }

        var instance = await readStore.GetInstanceWithNodeStatesAsync(executionId, cancellationToken);

        if (instance is null)
        {
            return;
        }

        if (!ShouldStartErrorWorkflow(instance.WorkflowDefinitionId.Value, instance.TriggerOrigin, defaultErrorWorkflowId))
        {
            _logger.LogDebug("Skipping error workflow for failed run {ExecutionId} (loop guard).", executionId);
            return;
        }

        var workflowStore = scope.ServiceProvider.GetRequiredService<IWorkflowStore>();
        var enqueuer = scope.ServiceProvider.GetRequiredService<IErrorWorkflowRunEnqueuer>();

        var workflow = await workflowStore.GetAsync(instance.WorkflowDefinitionId, cancellationToken);
        var context = await FailureContextBuilder.BuildAsync(readStore, instance, workflow, cancellationToken);
        var failedNodeType = ResolveFailedNodeType(workflow, context.FailedNodeId);

        var payload = BuildPayload(context, failedNodeType);
        var globals = BuildGlobals(context, failedNodeType);

        var errorRunId = await enqueuer.EnqueueAsync(
            new WorkflowDefinitionId(defaultErrorWorkflowId), executionId, payload, globals, cancellationToken);

        if (errorRunId is null)
        {
            _logger.LogWarning(
                "Error workflow '{ErrorWorkflowId}' is not published/active; no error run started for failed execution {ExecutionId}.",
                defaultErrorWorkflowId, executionId);
            return;
        }

        // Breadcrumb on the FAILED run pointing forward to its error-handler run.
        var handlerWorkflow = await workflowStore.GetAsync(new WorkflowDefinitionId(defaultErrorWorkflowId), cancellationToken);
        var handlerName = handlerWorkflow?.Name ?? defaultErrorWorkflowId;
        var journalWriter = scope.ServiceProvider.GetRequiredService<IExecutionJournalWriter>();
        var publisher = scope.ServiceProvider.GetRequiredService<IExecutionEventPublisher>();

        var forwardLink = new ExecutionJournal
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = executionId,
            NodeId = null,
            Timestamp = DateTimeOffset.UtcNow,
            EventType = "ErrorWorkflowStarted",
            Message = $"Error workflow '{handlerName}' started — run {errorRunId.Value.Value}.",
            Data = new Dictionary<string, object>
            {
                ["errorRunId"] = errorRunId.Value.Value.ToString(),
                ["errorWorkflowId"] = defaultErrorWorkflowId
            }
        };

        await journalWriter.WriteAsync(forwardLink);
        await publisher.PublishAsync(executionId, forwardLink, cancellationToken);
    }
}
