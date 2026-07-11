using System;
using KnotGarden.Core.Contracts;

namespace KnotGarden.Features.Polling;

internal static class PollValidator
{
    /// <summary>
    /// Change detection for a transport validator (ETag tag / Last-Modified date) carried in a 200 response.
    /// A missing validator falls back to "always new" so a run is never silently skipped.
    /// </summary>
    public static PollResult FromValidator(string? validator, string? cursor, string body)
    {
        if (string.IsNullOrEmpty(validator))
        {
            return new PollResult(HasNew: true, Payload: body, NewCursor: cursor);
        }

        var hasNew = !string.Equals(validator, cursor, StringComparison.Ordinal);
        return new PollResult(hasNew, Payload: hasNew ? body : null, NewCursor: validator);
    }
}
