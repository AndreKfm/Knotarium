using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Notifications;
using KnotGarden.Infrastructure.Persistence;

namespace KnotGarden.Api;

/// <summary>
/// Notification channel CRUD + live test — the failure-alert destinations (Webhook/Slack/Email).
/// Channel configs hold secrets, so they are stored encrypted and every response is masked; config
/// is optional on update so the name/default flag can change without re-entering secrets.
/// </summary>
public static class NotificationChannelEndpoints
{
    private static object MaskNotificationChannel(NotificationChannel channel) => new
    {
        id = channel.Id,
        name = channel.Name,
        type = channel.Type.ToString(),
        isDefaultFailureAlert = channel.IsDefaultFailureAlert,
        createdAt = channel.CreatedAt,
        updatedAt = channel.UpdatedAt
    };

    public static void MapNotificationChannelEndpoints(this WebApplication app)
    {
        app.MapGet("/api/notification-channels", async (AppDbContext db) =>
        {
            var list = await db.NotificationChannels.ToListAsync();
            return Results.Ok(list.Select(MaskNotificationChannel).ToList());
        });

        app.MapPost("/api/notification-channels", async (CreateNotificationChannelRequest request, AppDbContext db, ICredentialCipher cipher) =>
        {
            if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { message = "Id and Name are required" });
            }

            if (!Enum.TryParse<NotificationChannelType>(request.Type, ignoreCase: true, out var channelType))
            {
                return Results.BadRequest(new { message = $"Unknown channel type '{request.Type}'." });
            }

            var existing = await db.NotificationChannels.FindAsync(request.Id);

            // Config is optional on update so the default-flag/name can be changed without re-entering
            // secrets. A brand-new channel must supply its config.
            string encryptedConfig;
            if (request.Config.HasValue && request.Config.Value.ValueKind != JsonValueKind.Null && request.Config.Value.ValueKind != JsonValueKind.Undefined)
            {
                encryptedConfig = cipher.Encrypt(request.Config.Value.GetRawText());
            }
            else if (existing != null)
            {
                encryptedConfig = existing.EncryptedConfig;
            }
            else
            {
                return Results.BadRequest(new { message = "Config is required when creating a channel." });
            }

            var now = DateTimeOffset.UtcNow;
            if (existing != null)
            {
                existing.Name = request.Name;
                existing.Type = channelType;
                existing.EncryptedConfig = encryptedConfig;
                existing.IsDefaultFailureAlert = request.IsDefaultFailureAlert;
                existing.UpdatedAt = now;
                await db.SaveChangesAsync();
                return Results.Ok(MaskNotificationChannel(existing));
            }

            var channel = new NotificationChannel
            {
                Id = request.Id,
                Name = request.Name,
                Type = channelType,
                EncryptedConfig = encryptedConfig,
                IsDefaultFailureAlert = request.IsDefaultFailureAlert,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.NotificationChannels.Add(channel);
            await db.SaveChangesAsync();
            return Results.Created($"/api/notification-channels/{channel.Id}", MaskNotificationChannel(channel));
        });

        app.MapDelete("/api/notification-channels/{id}", async (string id, AppDbContext db) =>
        {
            var channel = await db.NotificationChannels.FindAsync(id);
            if (channel == null)
            {
                return Results.NotFound();
            }

            db.NotificationChannels.Remove(channel);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        app.MapPost("/api/notification-channels/{id}/test", async (string id, AppDbContext db, NotificationDispatcher dispatcher, CancellationToken cancellationToken) =>
        {
            var channel = await db.NotificationChannels.FindAsync(id);
            if (channel == null)
            {
                return Results.NotFound();
            }

            var sample = new FailureAlertMessage(
                WorkflowName: "Test Workflow",
                WorkflowId: "test-workflow",
                ExecutionId: Guid.NewGuid().ToString(),
                FailedNodeId: "test-node",
                ErrorMessage: "This is a test failure alert from KnotGarden.",
                TriggerOrigin: "test",
                TimestampUtc: DateTimeOffset.UtcNow).ToNotification();

            try
            {
                await dispatcher.SendAsync(channel, sample, cancellationToken);
                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { success = false, error = ex.Message });
            }
        });
    }
}
