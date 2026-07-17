// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Notifications;

/// <summary>
/// Posts the alert as a JSON document to a configured URL.
/// Config schema: <c>{ "url": "https://..." }</c>.
/// </summary>
public class WebhookNotificationSender : INotificationSender
{
    private readonly IHttpClientFactory _clientFactory;

    public WebhookNotificationSender(IHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public NotificationChannelType Type => NotificationChannelType.Webhook;

    public async Task SendAsync(JsonElement config, NotificationMessage message, CancellationToken cancellationToken)
    {
        var url = NotificationConfig.GetString(config, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("Webhook channel is missing the 'url' configuration value.");
        }

        // Title + body at the top level, plus any structured fields the caller supplied (e.g. the
        // failure-alert metadata) merged alongside them for machine consumers.
        var body = new Dictionary<string, object?>
        {
            ["title"] = message.Title,
            ["text"] = message.Body,
        };
        if (message.Data != null)
        {
            foreach (var kvp in message.Data)
            {
                body[kvp.Key] = kvp.Value;
            }
        }

        var payload = JsonSerializer.Serialize(body);

        var client = _clientFactory.CreateClient("NotificationWebhook");
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
