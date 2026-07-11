using System;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Domain;

namespace KnotGarden.Features.Notifications;

/// <summary>
/// Sends the alert as an e-mail over SMTP.
/// Config schema:
/// <c>{ "host", "port", "useSsl", "username", "password", "fromAddress", "toAddresses": [..] }</c>.
/// </summary>
public class EmailNotificationSender : INotificationSender
{
    public NotificationChannelType Type => NotificationChannelType.Email;

    public async Task SendAsync(JsonElement config, NotificationMessage message, CancellationToken cancellationToken)
    {
        var host = NotificationConfig.GetString(config, "host");
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("Email channel is missing the 'host' configuration value.");
        }

        var port = NotificationConfig.GetInt(config, "port") ?? 587;
        var useSsl = NotificationConfig.GetBool(config, "useSsl", defaultValue: true);
        var username = NotificationConfig.GetString(config, "username");
        var password = NotificationConfig.GetString(config, "password");
        var fromAddress = NotificationConfig.GetString(config, "fromAddress") ?? username;
        var toAddresses = NotificationConfig.GetStringList(config, "toAddresses");

        if (string.IsNullOrWhiteSpace(fromAddress))
        {
            throw new InvalidOperationException("Email channel is missing the 'fromAddress' configuration value.");
        }

        if (toAddresses.Count == 0)
        {
            throw new InvalidOperationException("Email channel has no recipients configured in 'toAddresses'.");
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(fromAddress),
            Subject = message.Title,
            Body = message.Body
        };

        foreach (var recipient in toAddresses)
        {
            mail.To.Add(recipient);
        }

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = useSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        if (!string.IsNullOrWhiteSpace(username))
        {
            client.Credentials = new NetworkCredential(username, password ?? string.Empty);
        }

        await client.SendMailAsync(mail, cancellationToken);
    }
}
