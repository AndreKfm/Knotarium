// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Ai;

public enum AiGenerationStatus
{
    Queued,
    Running,
    Succeeded,
    Failed
}

/// <summary>
/// One AI workflow-generation job. Interactive and ephemeral — held in an in-memory store (see
/// <see cref="AiGenerationJobStore"/>); v1 does not persist across restarts. On success it carries the
/// generated <see cref="Workflow"/> (topology only — the canvas assigns geometry on load) and the
/// <see cref="OpenSlots"/> the user must bind. On failure it carries either compiler/parse
/// <see cref="Diagnostics"/> (the loop gave up) or an <see cref="Error"/> (a transport/config failure).
/// </summary>
public sealed record AiGenerationJob
{
    public required string Id { get; init; }
    public required string Intent { get; init; }
    /// <summary>When set, this job MODIFIES the given workflow per the intent instead of generating fresh.</summary>
    public WorkflowDefinition? CurrentWorkflow { get; init; }
    public required AiGenerationStatus Status { get; init; }
    public WorkflowDefinition? Workflow { get; init; }
    public IReadOnlyList<string> OpenSlots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public int Attempts { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
