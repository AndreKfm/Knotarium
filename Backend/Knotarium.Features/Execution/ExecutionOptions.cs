// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using Microsoft.Extensions.Configuration;

namespace Knotarium.Features.Execution;

/// <summary>
/// Run-level execution knobs, bound from the <c>Execution</c> config section. All values are clamped
/// to safe ranges at bind time (<see cref="FromConfiguration"/>) so a mistyped config value can never
/// produce an unbounded worker.
/// </summary>
/// <remarks>
/// <see cref="MaxConcurrentRuns"/> mirrors the <c>parallelForEach</c> clamp precedent (1–64).
/// <c>1</c> reproduces the historical fully-serial drain exactly and is the kill-switch fallback.
/// The default of 4 is deliberately conservative: meaningful overlap for I/O-bound workflows while
/// keeping SQLite single-writer contention low (journal batching keeps the write side cheap).
/// </remarks>
public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";

    /// <summary>How many workflow runs may execute concurrently. 1 = serial (historical behavior).</summary>
    public int MaxConcurrentRuns { get; set; } = 4;

    /// <summary>
    /// Soft cap on queued-but-not-started runs. Externally-triggered start paths (manual run, webhook)
    /// are rejected with 429 once the queue is this deep; internal producers (recovery, resume,
    /// schedule/poll/error enqueuers whose runs are already persisted) are never rejected.
    /// </summary>
    public int MaxQueueDepth { get; set; } = 1000;

    /// <summary>How long a graceful shutdown waits for in-flight runs before letting crash recovery take over.</summary>
    public int ShutdownDrainTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Batch journal-entry INSERTs into one multi-row transaction (count + time bounded). Durability-critical
    /// entries (suspend/terminal/external-effect protocol) are always awaited to disk regardless.
    /// </summary>
    public bool JournalBatchingEnabled { get; set; } = true;

    /// <summary>Flush the journal buffer at this many entries, whichever bound fires first.</summary>
    public int JournalBatchMaxSize { get; set; } = 32;

    /// <summary>Flush the journal buffer at most this many milliseconds after its first entry.</summary>
    public int JournalBatchMaxDelayMilliseconds { get; set; } = 25;

    /// <summary>Bind the <c>Execution</c> section (missing section → defaults) and clamp every knob.</summary>
    public static ExecutionOptions FromConfiguration(IConfiguration? configuration)
    {
        var options = configuration?.GetSection(SectionName).Get<ExecutionOptions>() ?? new ExecutionOptions();

        options.MaxConcurrentRuns = Math.Clamp(options.MaxConcurrentRuns, 1, 64);
        options.MaxQueueDepth = Math.Clamp(options.MaxQueueDepth, 1, 100_000);
        options.ShutdownDrainTimeoutSeconds = Math.Clamp(options.ShutdownDrainTimeoutSeconds, 0, 300);
        options.JournalBatchMaxSize = Math.Clamp(options.JournalBatchMaxSize, 1, 256);
        options.JournalBatchMaxDelayMilliseconds = Math.Clamp(options.JournalBatchMaxDelayMilliseconds, 1, 1000);

        return options;
    }
}
