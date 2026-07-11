namespace Knotarium.Core.Domain;

/// <summary>
/// The transport a <see cref="NotificationChannel"/> uses to deliver an alert.
/// </summary>
public enum NotificationChannelType
{
    /// <summary>Generic HTTP POST of the alert payload as JSON to a configured URL.</summary>
    Webhook = 0,

    /// <summary>Slack incoming-webhook delivery with block-formatted message.</summary>
    Slack = 1,

    /// <summary>E-mail delivery over SMTP.</summary>
    Email = 2
}
