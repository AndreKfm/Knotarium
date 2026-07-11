using System;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Execution;
using Xunit;

namespace KnotGarden.Tests.Execution;

public class RetryBackoffCalculatorTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    public void CalculateDelay_WithoutJitter_UsesExponentialBackoff(int attemptNumber, int expectedSeconds)
    {
        var policy = new RetryPolicy(MaxAttempts: 5, InitialDelaySeconds: 2, BackoffRate: 2.0, Jitter: false, MaxDelaySeconds: 30);

        var delay = RetryBackoffCalculator.CalculateDelay(policy, attemptNumber);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void CalculateDelay_ExceedingMaxDelay_CapsDelay()
    {
        var policy = new RetryPolicy(MaxAttempts: 5, InitialDelaySeconds: 10, BackoffRate: 3.0, Jitter: false, MaxDelaySeconds: 30);

        var delay = RetryBackoffCalculator.CalculateDelay(policy, attemptNumber: 4);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }
}