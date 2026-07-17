// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Contracts;

namespace Knotarium.Features.Polling;

internal static class PollStrategyParser
{
    public static PollChangeDetection Parse(string? raw) => raw switch
    {
        "etag" => PollChangeDetection.Etag,
        "last-modified" => PollChangeDetection.LastModified,
        "hash" => PollChangeDetection.Hash,
        "json-cursor" => PollChangeDetection.JsonCursor,
        "always" => PollChangeDetection.Always,
        _ => PollChangeDetection.Hash // safe default: body-hash dedup needs no server support and never floods runs
    };
}
