using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Delivers a <see cref="NotificationMessage"/> over a configured <see cref="NotificationChannel"/>,
/// decrypting the channel config and routing to the matching sender. Lets a node send a notification
/// without depending on the Notifications slice that owns the dispatcher and its senders.
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>Delivers <paramref name="message"/> over <paramref name="channel"/>. Throws on delivery failure.</summary>
    Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken cancellationToken);
}
