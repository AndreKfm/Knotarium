using KnotGarden.Core.Contracts;

namespace KnotGarden.Features.Polling;

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
