// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Knotarium.Core.Domain;

public enum WorkItemStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3
}

public sealed class ExecutionWorkItem
{
    public Guid Id { get; set; }
    public ExecutionInstanceId ExecutionInstanceId { get; set; }
    public string Type { get; set; } = null!; // "Resume", "Retry", or "ManualDecision"
    public string Payload { get; set; } = null!; // JSON context parameters
    public DateTimeOffset? NotBeforeUtc { get; set; } // Scheduled due time for background queue polling
    public WorkItemStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
}
