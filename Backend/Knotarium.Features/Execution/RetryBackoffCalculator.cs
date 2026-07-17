// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Execution;

/// <summary>
/// Calculates retry delays from a manifest retry policy using 1-indexed attempt numbers.
/// </summary>
public static class RetryBackoffCalculator
{
    /// <summary>
    /// Calculates the delay for the supplied attempt number.
    /// </summary>
    /// <param name="policy">The retry policy that defines backoff behavior.</param>
    /// <param name="attemptNumber">The 1-indexed attempt number where 1 represents the initial execution.</param>
    /// <returns>The delay to apply before the specified attempt executes.</returns>
    public static TimeSpan CalculateDelay(RetryPolicy policy, int attemptNumber)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (attemptNumber <= 1)
        {
            return TimeSpan.Zero;
        }

        var retryCount = attemptNumber - 1;
        var delaySeconds = policy.InitialDelaySeconds * Math.Pow(policy.BackoffRate, retryCount - 1);

        if (policy.Jitter)
        {
            var jitterFactor = 1d + ((Random.Shared.NextDouble() * 2d) - 1d) * 0.15d;
            delaySeconds *= jitterFactor;
        }

        delaySeconds = Math.Clamp(delaySeconds, 0d, policy.MaxDelaySeconds);
        return TimeSpan.FromSeconds(delaySeconds);
    }
}