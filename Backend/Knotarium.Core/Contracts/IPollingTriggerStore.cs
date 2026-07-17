// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Read/persist seam over polling triggers, so the polling evaluation service can find due triggers and
/// persist their advanced cursor/schedule fields without binding the concrete <c>AppDbContext</c>. The
/// EF adapter lives in Infrastructure.
/// </summary>
public interface IPollingTriggerStore
{
    /// <summary>
    /// Active triggers due at or before <paramref name="now"/> whose owning workflow is enabled,
    /// ordered earliest-first.
    /// </summary>
    Task<IReadOnlyList<PollingTrigger>> GetDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Persists the mutated scheduling/cursor fields of a trigger (cursor, next/last poll, last error).</summary>
    Task SaveAsync(PollingTrigger trigger, CancellationToken cancellationToken = default);
}
