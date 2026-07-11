using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Knotarium.Features.Execution;

public sealed class ExecutionTelemetry : IOutboundHttpTelemetry, IDisposable
{
    public const string MeterName = "Knotarium.Execution";
    public const string ActivitySourceName = "Knotarium.Execution";

    private readonly Meter _meter;
    private readonly ActivitySource _activitySource;
    private readonly IServiceScopeFactory? _scopeFactory;

    private readonly Counter<long> _executionsStarted;
    private readonly Counter<long> _executionsCompleted;
    private readonly Counter<long> _executionsFailed;
    private readonly Counter<long> _journalWrites;
    private readonly Histogram<double> _nodeExecutionDurationSeconds;
    private readonly Histogram<double> _journalWriteLatencySeconds;

    private long _runningExecutions;

    public ExecutionTelemetry()
    {
        _meter = new Meter(MeterName);
        _activitySource = new ActivitySource(ActivitySourceName);
        _executionsStarted = _meter.CreateCounter<long>("executions_started_total");
        _executionsCompleted = _meter.CreateCounter<long>("executions_completed_total");
        _executionsFailed = _meter.CreateCounter<long>("executions_failed_total");
        _journalWrites = _meter.CreateCounter<long>("journal_writes_total");
        _nodeExecutionDurationSeconds = _meter.CreateHistogram<double>("node_execution_duration_seconds", unit: "s");
        _journalWriteLatencySeconds = _meter.CreateHistogram<double>("journal_write_latency_seconds", unit: "s");

        _meter.CreateObservableGauge<long>(
            "running_executions",
            () => new[] { new Measurement<long>(Interlocked.Read(ref _runningExecutions)) });
    }

    public ExecutionTelemetry(IServiceScopeFactory scopeFactory)
        : this()
    {
        _scopeFactory = scopeFactory;

        _meter.CreateObservableGauge<long>(
            "loaded_node_packages",
            ObserveLoadedNodePackages);
    }

    public void RecordExecutionStarted()
    {
        _executionsStarted.Add(1);
        Interlocked.Increment(ref _runningExecutions);
    }

    public void RecordExecutionCompleted()
    {
        _executionsCompleted.Add(1);
        Interlocked.Decrement(ref _runningExecutions);
    }

    public void RecordExecutionFailed()
    {
        _executionsFailed.Add(1);
        Interlocked.Decrement(ref _runningExecutions);
    }

    public void RecordExecutionStopped()
    {
        Interlocked.Decrement(ref _runningExecutions);
    }

    public void RecordNodeExecutionDuration(string nodeType, TimeSpan elapsed)
    {
        _nodeExecutionDurationSeconds.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("node_type", nodeType));
    }

    public void RecordJournalWriteLatency(TimeSpan elapsed)
    {
        _journalWrites.Add(1);
        _journalWriteLatencySeconds.Record(elapsed.TotalSeconds);
    }

    public Activity? StartWorkflowActivity(ExecutionInstance instance, string phase)
    {
        var activity = _activitySource.StartActivity("workflow.execute", ActivityKind.Internal);
        activity?.SetTag("execution.id", instance.Id.Value.ToString());
        activity?.SetTag("workflow.id", instance.WorkflowDefinitionId.Value);
        activity?.SetTag("execution.phase", phase);
        return activity;
    }

    public Activity? StartNodeActivity(ExecutionInstanceId executionId, NodeId nodeId, string nodeType)
    {
        var activity = _activitySource.StartActivity("workflow.node.execute", ActivityKind.Internal);
        activity?.SetTag("execution.id", executionId.Value.ToString());
        activity?.SetTag("node.id", nodeId.Value);
        activity?.SetTag("node.type", nodeType);
        return activity;
    }

    public Activity? StartOutboundHttpActivity(Uri uri, string method, NodeExecutionContext context)
    {
        var activity = _activitySource.StartActivity("workflow.capability.http", ActivityKind.Client);
        activity?.SetTag("execution.id", context.ExecutionId.ToString());
        activity?.SetTag("node.id", context.NodeId.Value);
        activity?.SetTag("http.method", method);
        activity?.SetTag("server.address", uri.Host);
        activity?.SetTag("url.full", uri.ToString());
        return activity;
    }

    private IEnumerable<Measurement<long>> ObserveLoadedNodePackages()
    {
        if (_scopeFactory == null)
        {
            return new[] { new Measurement<long>(0) };
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return new[] { new Measurement<long>(dbContext.NodePackages.LongCount()) };
        }
        catch
        {
            return new[] { new Measurement<long>(0) };
        }
    }

    public void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
    }
}