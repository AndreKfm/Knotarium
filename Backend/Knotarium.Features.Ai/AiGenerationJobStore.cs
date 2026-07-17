// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Ai;

/// <summary>
/// Thread-safe in-memory store of <see cref="AiGenerationJob"/>s, shared (singleton) between the API
/// endpoints that create/poll jobs and the worker that runs them. Jobs are interactive and short-lived,
/// so v1 keeps them in process rather than in the database (swap to a table if they must survive a
/// restart or be auditable — see the design doc's deferred list).
/// </summary>
public sealed class AiGenerationJobStore
{
    private readonly ConcurrentDictionary<string, AiGenerationJob> _jobs = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    public AiGenerationJobStore() : this(() => DateTimeOffset.UtcNow) { }

    // Clock seam for deterministic tests.
    public AiGenerationJobStore(Func<DateTimeOffset> clock) => _clock = clock;

    public AiGenerationJob Create(string intent, WorkflowDefinition? currentWorkflow = null)
    {
        var now = _clock();
        var job = new AiGenerationJob
        {
            Id = Guid.NewGuid().ToString("n"),
            Intent = intent,
            CurrentWorkflow = currentWorkflow,
            Status = AiGenerationStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now
        };
        _jobs[job.Id] = job;
        return job;
    }

    public AiGenerationJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public void MarkRunning(string id) =>
        Mutate(id, job => job with { Status = AiGenerationStatus.Running });

    public void MarkSucceeded(string id, WorkflowDefinition workflow, IReadOnlyList<string> openSlots, int attempts) =>
        Mutate(id, job => job with
        {
            Status = AiGenerationStatus.Succeeded,
            Workflow = workflow,
            OpenSlots = openSlots,
            Attempts = attempts
        });

    /// <summary>Mark failed with compiler/parse diagnostics (the repair loop gave up).</summary>
    public void MarkFailed(string id, IReadOnlyList<string> diagnostics, int attempts) =>
        Mutate(id, job => job with
        {
            Status = AiGenerationStatus.Failed,
            Diagnostics = diagnostics,
            Attempts = attempts
        });

    /// <summary>Mark failed with a transport/configuration error (an exception escaped the run).</summary>
    public void MarkFailed(string id, string error) =>
        Mutate(id, job => job with { Status = AiGenerationStatus.Failed, Error = error });

    private void Mutate(string id, Func<AiGenerationJob, AiGenerationJob> mutate)
    {
        _jobs.AddOrUpdate(
            id,
            // A missing id is a no-op (the worker may race a never-created id); return a placeholder that
            // the absence of a prior value makes unreachable in practice.
            _ => throw new InvalidOperationException($"Generation job '{id}' does not exist."),
            (_, existing) => mutate(existing) with { UpdatedAt = _clock() });
    }
}
