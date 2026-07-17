// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts;

/// <summary>How a poll decides whether the source has new data since the last poll.</summary>
public enum PollChangeDetection
{
    Etag,
    LastModified,
    Hash,
    JsonCursor,
    Always
}

/// <summary>Inputs to a single poll: the source-specific config and the opaque prior cursor.</summary>
public sealed record PollContext(string ConfigJson, string? Cursor);

/// <summary>
/// Outcome of a single poll. <see cref="HasNew"/> drives whether a run is started;
/// <see cref="NewCursor"/> replaces the stored cursor when (and only when) new data is observed.
/// </summary>
public sealed record PollResult(bool HasNew, object? Payload, string? NewCursor);

/// <summary>A pluggable poll source. Implementations are resolved by <see cref="Kind"/>.</summary>
public interface IPollSource
{
    string Kind { get; }
    Task<PollResult> PollAsync(PollContext context, CancellationToken cancellationToken);
}
