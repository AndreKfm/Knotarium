using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Notifications;

/// <summary>
/// Posts a block-formatted message to a Slack incoming webhook.
/// Config schema: <c>{ "webhookUrl": "https://hooks.slack.com/services/..." }</c>.
/// </summary>
public class SlackNotificationSender : INotificationSender
{
    private readonly IHttpClientFactory _clientFactory;

    public SlackNotificationSender(IHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public NotificationChannelType Type => NotificationChannelType.Slack;

    public async Task SendAsync(JsonElement config, NotificationMessage message, CancellationToken cancellationToken)
    {
        var webhookUrl = NotificationConfig.GetString(config, "webhookUrl");
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            throw new InvalidOperationException("Slack channel is missing the 'webhookUrl' configuration value.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            text = message.Title,
            blocks = new object[]
            {
                new
                {
                    type = "header",
                    text = new { type = "plain_text", text = message.Title, emoji = true }
                },
                new
                {
                    type = "section",
                    text = new { type = "mrkdwn", text = message.Body }
                }
            }
        });

        var client = _clientFactory.CreateClient("NotificationSlack");
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(webhookUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
