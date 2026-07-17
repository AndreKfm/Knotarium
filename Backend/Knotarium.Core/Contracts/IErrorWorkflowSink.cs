// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Producer side of the non-blocking hand-off of a failed execution id from the workflow executor to
/// the error-workflow dispatch worker (Notifications slice), which starts the global error-handler
/// workflow. Sibling of <see cref="IFailureAlertSink"/>: exposing only the enqueue lets the Execution
/// slice signal a failure without depending on Notifications; the draining worker keeps the concrete
/// queue. Optional at the executor — a null sink means no error workflow is wired.
/// </summary>
public interface IErrorWorkflowSink
{
    /// <summary>Enqueues a failed execution for error-workflow dispatch. Non-blocking; safe on the hot path.</summary>
    void Enqueue(ExecutionInstanceId executionId);
}
