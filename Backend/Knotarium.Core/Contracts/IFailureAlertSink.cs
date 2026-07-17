// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Producer side of the non-blocking hand-off of a failed execution id from the workflow executor to
/// the failure-alert dispatch worker (Notifications slice). Exposing only the enqueue lets the
/// Execution slice signal a failure without depending on Notifications; the draining worker keeps a
/// reference to the concrete queue. Optional at the executor — a null sink means no alerting is wired.
/// </summary>
public interface IFailureAlertSink
{
    /// <summary>Enqueues a failed execution for alert dispatch. Non-blocking; safe on the hot path.</summary>
    void Enqueue(ExecutionInstanceId executionId);
}
