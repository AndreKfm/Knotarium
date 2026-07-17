// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Read-side seam over stored notification channels, so the Send Notification node and the
/// failure-alert channel resolver can look channels up without binding the concrete <c>AppDbContext</c>.
/// The EF adapter lives in Infrastructure.
/// </summary>
public interface INotificationChannelStore
{
    /// <summary>The channel with the given id, or null when none matches.</summary>
    Task<NotificationChannel?> GetAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>All configured notification channels.</summary>
    Task<IReadOnlyList<NotificationChannel>> ListAsync(CancellationToken cancellationToken = default);
}
