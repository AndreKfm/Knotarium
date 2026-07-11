using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Domain;

namespace KnotGarden.Features.Notifications;

/// <summary>
/// Delivers a <see cref="FailureAlertMessage"/> over a single transport. Implementations are
/// selected by <see cref="Type"/>; the worker decrypts the channel's stored configuration and
/// hands it in as a parsed JSON object whose schema is transport-specific.
/// </summary>
public interface INotificationSender
{
    /// <summary>The channel transport this sender handles.</summary>
    NotificationChannelType Type { get; }

    /// <summary>
    /// Sends the alert. May throw on delivery failure — callers are expected to catch and
    /// record the outcome rather than letting it bubble into workflow execution.
    /// </summary>
    /// <param name="config">Decrypted, transport-specific configuration (e.g. <c>{ "url": "..." }</c>).</param>
    Task SendAsync(JsonElement config, NotificationMessage message, CancellationToken cancellationToken);
}
