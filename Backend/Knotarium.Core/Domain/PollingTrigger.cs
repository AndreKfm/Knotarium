// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Knotarium.Core.Domain;

/// <summary>
/// A persisted polling trigger derived from a pollingTrigger node. Mirrors <see cref="Schedule"/>
/// but adds change-detection cursor state and source configuration.
/// </summary>
public sealed class PollingTrigger
{
    public Guid Id { get; set; }
    public WorkflowDefinitionId WorkflowDefinitionId { get; set; }
    public int IntervalSeconds { get; set; }
    public DateTimeOffset NextPollAtUtc { get; set; } // Tracked and evaluated in UTC
    public string ConfigJson { get; set; } = null!;   // sourceKind + change-detection + source fields
    public string? Cursor { get; set; }               // opaque last-seen state (etag/hash/json value)
    public bool IsActive { get; set; }
    public DateTimeOffset? LastPolledAtUtc { get; set; }
    public string? LastError { get; set; }
}
