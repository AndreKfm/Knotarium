// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;

namespace Knotarium.Api.Services;

/// <summary>
/// Runs a thin polling loop that delegates due schedule evaluation to the Features layer.
/// </summary>
public sealed partial class SchedulingWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceProvider _serviceProvider;
    private readonly RuntimeArmingState _armingState;
    private readonly ILogger<SchedulingWorker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchedulingWorker"/> class.
    /// </summary>
    /// <param name="serviceProvider">The root service provider.</param>
    /// <param name="armingState">The global runtime arming switch.</param>
    /// <param name="logger">The logger.</param>
    public SchedulingWorker(IServiceProvider serviceProvider, RuntimeArmingState armingState, ILogger<SchedulingWorker> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _armingState = armingState ?? throw new ArgumentNullException(nameof(armingState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var pollTimer = new PeriodicTimer(PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Disarmed = design-time / editing mode: pause all automatic schedule
                // evaluation. Manual triggers still run via their own endpoints.
                if (_armingState.IsArmed)
                {
                    using var scope = _serviceProvider.CreateScope();
                    var evaluator = scope.ServiceProvider.GetRequiredService<IScheduleEvaluationService>();
                    await evaluator.EvaluateActiveSchedulesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Log.UnhandledWorkerException(_logger, exception);
            }

            try
            {
                await pollTimer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1200, Level = LogLevel.Error, Message = "Scheduling worker encountered an unhandled exception.")]
        public static partial void UnhandledWorkerException(ILogger logger, Exception exception);
    }
}