using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Knotarium.Features.Ai;

/// <summary>
/// Background service that drains <see cref="AiGenerationQueue"/> and runs each generation job through a
/// scoped <see cref="IAiGenerationRunner"/>, recording the terminal state in <see cref="AiGenerationJobStore"/>.
/// Mirrors <c>FailureAlertWorker</c>: a per-job scope keeps the DB context / generator fresh, and the loop
/// is exception-guarded so a single failed job never crashes the worker.
/// </summary>
public sealed class AiGenerationWorker : BackgroundService
{
    private readonly AiGenerationQueue _queue;
    private readonly AiGenerationJobStore _store;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AiGenerationWorker> _logger;

    public AiGenerationWorker(
        AiGenerationQueue queue,
        AiGenerationJobStore store,
        IServiceProvider serviceProvider,
        ILogger<AiGenerationWorker> logger)
    {
        _queue = queue;
        _store = store;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AI Generation Worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            string jobId;
            try
            {
                jobId = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessAsync(jobId, stoppingToken);
        }

        _logger.LogInformation("AI Generation Worker stopped.");
    }

    /// <summary>
    /// Run one job to a terminal state. Public so it can be driven directly in tests without the hosted
    /// loop. Never throws — any failure is recorded on the job.
    /// </summary>
    public async Task ProcessAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = _store.Get(jobId);
        if (job is null)
        {
            _logger.LogWarning("AI generation job {JobId} not found; skipping.", jobId);
            return;
        }

        try
        {
            _store.MarkRunning(jobId);

            using var scope = _serviceProvider.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IAiGenerationRunner>();
            var result = await runner.RunAsync(job.Intent, cancellationToken, job.CurrentWorkflow);

            if (result.Succeeded && result.Workflow is not null)
            {
                _store.MarkSucceeded(jobId, result.Workflow, result.OpenSlots, result.Attempts);
            }
            else
            {
                _store.MarkFailed(jobId, result.Diagnostics, result.Attempts);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Transport/config failure (missing key, non-2xx, egress block) or anything unexpected:
            // record it on the job and keep the worker alive.
            _logger.LogError(ex, "AI generation job {JobId} failed.", jobId);
            _store.MarkFailed(jobId, ex.Message);
        }
    }
}
