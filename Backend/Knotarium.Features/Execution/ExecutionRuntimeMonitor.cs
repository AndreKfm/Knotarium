// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;

namespace Knotarium.Features.Execution;

/// <summary>
/// Live run-level execution counters, updated by the worker and read by the runtime diagnostics
/// endpoint (and exportable as metrics). Singleton; all mutations are interlocked so concurrent
/// runs can report without coordination.
/// </summary>
public sealed class ExecutionRuntimeMonitor
{
    private long _inFlightRuns;
    private long _rejectedStarts;

    /// <summary>Runs (fresh dispatches + gated work-item resumes) currently holding a run slot.</summary>
    public long InFlightRuns => Interlocked.Read(ref _inFlightRuns);

    /// <summary>Start requests rejected with 429 because the execution queue was at its depth cap.</summary>
    public long RejectedStarts => Interlocked.Read(ref _rejectedStarts);

    public void RunStarted() => Interlocked.Increment(ref _inFlightRuns);

    public void RunFinished() => Interlocked.Decrement(ref _inFlightRuns);

    public void StartRejected() => Interlocked.Increment(ref _rejectedStarts);
}
