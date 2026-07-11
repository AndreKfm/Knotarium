using System;
using System.Collections.Generic;
using System.Linq;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Notifications;

/// <summary>
/// Pure resolution of which channels should receive a failure alert for a workflow, given its
/// per-workflow routing config and the full set of configured channels. Kept side-effect free so
/// the routing semantics can be unit-tested without a database.
/// </summary>
public static class FailureAlertChannelResolver
{
    public static IReadOnlyList<NotificationChannel> Resolve(
        FailureAlertConfig? config,
        IReadOnlyList<NotificationChannel> allChannels)
    {
        var mode = config?.Mode ?? FailureAlertModes.Inherit;

        if (string.Equals(mode, FailureAlertModes.Off, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<NotificationChannel>();
        }

        if (string.Equals(mode, FailureAlertModes.Custom, StringComparison.OrdinalIgnoreCase))
        {
            var ids = new HashSet<string>(config?.ChannelIds ?? Enumerable.Empty<string>());
            return allChannels.Where(c => ids.Contains(c.Id)).ToList();
        }

        // Inherit / unknown mode → global default channels.
        return allChannels.Where(c => c.IsDefaultFailureAlert).ToList();
    }
}
