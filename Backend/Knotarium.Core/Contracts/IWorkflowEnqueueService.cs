// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Represents the outcome of attempting to enqueue a scheduled workflow fire.
/// </summary>
public enum ScheduleEnqueueResult
{
    /// <summary>
    /// The fire slot was claimed and an execution was created.
    /// </summary>
    Enqueued,

    /// <summary>
    /// The fire slot was already claimed by another worker.
    /// </summary>
    DuplicateClaim,

    /// <summary>
    /// The workflow has no active runtime version and therefore cannot be enqueued.
    /// </summary>
    NoActiveVersion
}

/// <summary>
/// Claims due schedule fires and creates workflow executions without double-enqueueing the same planned fire slot.
/// </summary>
public interface IWorkflowEnqueueService
{
    /// <summary>
    /// Attempts to claim a schedule fire slot and create a pending workflow execution for it.
    /// </summary>
    /// <param name="scheduleId">The schedule being evaluated.</param>
    /// <param name="plannedFireAtUtc">The due fire slot being claimed.</param>
    /// <param name="nextFireAtUtc">The next computed fire slot to persist on the schedule when the claim succeeds.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The enqueue result for the evaluated schedule fire slot.</returns>
    Task<ScheduleEnqueueResult> ClaimAndEnqueueScheduleAsync(
        Guid scheduleId,
        DateTimeOffset plannedFireAtUtc,
        DateTimeOffset nextFireAtUtc,
        CancellationToken cancellationToken = default);
}