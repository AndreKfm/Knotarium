// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Sends a notification over a configured <see cref="Core.Domain.NotificationChannel"/> from within a
/// workflow (e.g. a success message, a status update, or an alert on a failure edge). Reuses the same
/// channel storage and sender layer as the automatic failure-alert hook, so secrets stay encrypted
/// and never appear in the workflow definition.
/// </summary>
public class SendNotificationNodeTask : INodeTask
{
    private readonly INotificationChannelStore _channelStore;
    private readonly INotificationDispatcher _dispatcher;

    public SendNotificationNodeTask(INotificationChannelStore channelStore, INotificationDispatcher dispatcher)
    {
        _channelStore = channelStore;
        _dispatcher = dispatcher;
    }

    private static string AsString(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        JsonElement el => el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : el.ToString(),
        _ => value.ToString() ?? string.Empty
    };

    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var channelId = context.Inputs.TryGetValue("channelId", out var channelObj) ? AsString(channelObj) : string.Empty;
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return new LegacyNodeResult.Failure("Send Notification failed: no channel selected ('channelId' is required).");
        }

        var message = context.Inputs.TryGetValue("message", out var messageObj) ? AsString(messageObj) : string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            return new LegacyNodeResult.Failure("Send Notification failed: 'message' is required.");
        }

        var subject = context.Inputs.TryGetValue("subject", out var subjectObj) ? AsString(subjectObj) : string.Empty;
        if (string.IsNullOrWhiteSpace(subject))
        {
            subject = "Workflow Notification";
        }

        var channel = await _channelStore.GetAsync(channelId, cancellationToken);

        if (channel == null)
        {
            return new LegacyNodeResult.Failure($"Send Notification failed: channel '{channelId}' not found.");
        }

        try
        {
            await _dispatcher.SendAsync(channel, new NotificationMessage(subject, message), cancellationToken);
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"Send Notification failed via channel '{channel.Name}': {ex.Message}");
        }

        return new LegacyNodeResult.Success(new Dictionary<string, object>
        {
            ["result"] = new Dictionary<string, object>
            {
                ["sent"] = true,
                ["channel"] = channel.Name
            }
        });
    }
}
