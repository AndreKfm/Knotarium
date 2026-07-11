using System;

namespace Knotarium.Core.Domain;

/// <summary>
/// A configured destination for failure alerts (and, later, inline notification nodes).
/// The transport-specific configuration — including any secrets such as SMTP passwords or
/// webhook URLs — is stored encrypted in <see cref="EncryptedConfig"/> as a JSON object and
/// decrypted on use via the shared credential cipher.
/// </summary>
public class NotificationChannel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public NotificationChannelType Type { get; set; }

    /// <summary>Encrypted JSON blob holding the transport-specific config (see the senders for the schema per type).</summary>
    public string EncryptedConfig { get; set; } = string.Empty;

    /// <summary>When true, this channel receives alerts for any workflow that does not override its failure-alert routing.</summary>
    public bool IsDefaultFailureAlert { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
