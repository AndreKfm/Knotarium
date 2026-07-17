// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Knotarium.Core.Contracts;

public abstract record LegacyNodeResult
{
    private LegacyNodeResult() { }

    public record Success(Dictionary<string, object>? Outputs = null) : LegacyNodeResult;

    /// <param name="ErrorMessage">Human-readable failure text (also surfaced on the node state).</param>
    /// <param name="ErrorCode">Optional machine-queryable code (e.g. a condition <c>ConditionErrorCode</c>).
    /// When set it is written as a discrete <c>errorCode</c> field in the failure journal entry's Data so
    /// the hash-chained audit can filter by code rather than substring-matching the message (R6). Null for
    /// failures that carry no structured code.</param>
    public record Failure(string ErrorMessage, string? ErrorCode = null) : LegacyNodeResult;
    public record WaitForEvent(string EventName) : LegacyNodeResult;

    /// <summary>
    /// Suspend the run for <paramref name="DurationMs"/> WITHOUT blocking the executor: the engine parks
    /// the node and schedules a timed resume (a "Resume" work item with NotBeforeUtc), freeing the single
    /// worker so other queued runs proceed. Returned by the Delay node for non-trivial waits; short waits
    /// still block inline (cheaper than a suspend/resume round-trip).
    /// </summary>
    public record Delay(int DurationMs) : LegacyNodeResult;
}
