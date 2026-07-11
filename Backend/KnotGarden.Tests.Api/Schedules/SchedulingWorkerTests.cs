using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using KnotGarden.Api.Services;
using KnotGarden.Core.Contracts;
using Xunit;

namespace KnotGarden.Tests.Schedules;

public class SchedulingWorkerTests
{
    [Fact]
    public async Task StartAsync_WhenArmed_ResolvesEvaluatorAndRunsEvaluationCycle()
    {
        var evaluator = new RecordingScheduleEvaluationService(throwOnInvoke: false);
        using var provider = BuildServiceProvider(evaluator);
        using var worker = new SchedulingWorker(provider, new RuntimeArmingState(initiallyArmed: true), NullLogger<SchedulingWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await evaluator.Invocation.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, evaluator.CallCount);
    }

    [Fact]
    public async Task StartAsync_WhenArmed_EvaluatorThrows_WorkerSwallowsFailureAndStopsCleanly()
    {
        var evaluator = new RecordingScheduleEvaluationService(throwOnInvoke: true);
        using var provider = BuildServiceProvider(evaluator);
        using var worker = new SchedulingWorker(provider, new RuntimeArmingState(initiallyArmed: true), NullLogger<SchedulingWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await evaluator.Invocation.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, evaluator.CallCount);
    }

    [Fact]
    public async Task StartAsync_WhenDisarmed_SkipsScheduleEvaluation()
    {
        var evaluator = new RecordingScheduleEvaluationService(throwOnInvoke: false);
        using var provider = BuildServiceProvider(evaluator);
        using var worker = new SchedulingWorker(provider, new RuntimeArmingState(initiallyArmed: false), NullLogger<SchedulingWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        // The worker evaluates immediately on the first loop iteration when armed; a
        // short wait is enough to prove it does NOT evaluate while disarmed.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, evaluator.CallCount);
    }

    private static ServiceProvider BuildServiceProvider(RecordingScheduleEvaluationService evaluator)
    {
        var services = new ServiceCollection();
        services.AddSingleton(evaluator);
        services.AddScoped<IScheduleEvaluationService>(serviceProvider => serviceProvider.GetRequiredService<RecordingScheduleEvaluationService>());
        return services.BuildServiceProvider();
    }

    private sealed class RecordingScheduleEvaluationService : IScheduleEvaluationService
    {
        private readonly bool _throwOnInvoke;

        public RecordingScheduleEvaluationService(bool throwOnInvoke)
        {
            _throwOnInvoke = throwOnInvoke;
        }

        public int CallCount { get; private set; }

        public TaskCompletionSource<bool> Invocation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EvaluateActiveSchedulesAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            Invocation.TrySetResult(true);

            if (_throwOnInvoke)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.CompletedTask;
        }
    }
}