using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Notifications;

/// <summary>
/// Decrypts a channel's stored configuration and routes a <see cref="FailureAlertMessage"/> to the
/// matching <see cref="INotificationSender"/>. Shared by the background failure-alert worker and the
/// "send test" API endpoint.
/// </summary>
public class NotificationDispatcher : INotificationDispatcher
{
    private readonly IReadOnlyList<INotificationSender> _senders;
    private readonly ICredentialCipher _cipher;

    public NotificationDispatcher(IEnumerable<INotificationSender> senders, ICredentialCipher cipher)
    {
        _senders = senders.ToList();
        _cipher = cipher;
    }

    /// <summary>
    /// Delivers <paramref name="message"/> over <paramref name="channel"/>. Throws on delivery
    /// failure (or when no sender is registered for the channel type) — callers record the outcome.
    /// </summary>
    public async Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken cancellationToken)
    {
        var sender = _senders.FirstOrDefault(s => s.Type == channel.Type)
            ?? throw new System.InvalidOperationException($"No notification sender registered for channel type '{channel.Type}'.");

        var json = _cipher.Decrypt(channel.EncryptedConfig);
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        await sender.SendAsync(document.RootElement, message, cancellationToken);
    }
}
