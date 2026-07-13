using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Knotarium.Api.Services;
using Knotarium.Api.Services.Auth;

namespace Knotarium.Api;

/// <summary>
/// Manage the optional per-workflow webhook secret guarding the anonymous <c>POST /api/executions</c>
/// trigger. Setting/clearing a secret is an admin action; the raw value is never returned (only whether
/// one is configured). See <see cref="WebhookSecretService"/> for the storage/verification model.
/// </summary>
public static class WebhookSecretEndpoints
{
    public static void MapWebhookSecretEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workflows/{id}/webhook-secret", async (string id, WebhookSecretService secrets, CancellationToken ct) =>
            Results.Ok(new { configured = await secrets.HasSecretAsync(id, ct) }));

        app.MapPut("/api/workflows/{id}/webhook-secret", async (string id, SetWebhookSecretRequest request, WebhookSecretService secrets, AuthOptions auth, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (auth.RequireAdmin(user) is { } denied) return denied;
            if (request is null || string.IsNullOrWhiteSpace(request.Secret))
            {
                return Results.BadRequest(new { message = "A non-empty 'secret' is required." });
            }
            await secrets.SetAsync(id, request.Secret, ct);
            return Results.Ok(new { configured = true });
        });

        app.MapDelete("/api/workflows/{id}/webhook-secret", async (string id, WebhookSecretService secrets, AuthOptions auth, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (auth.RequireAdmin(user) is { } denied) return denied;
            var removed = await secrets.ClearAsync(id, ct);
            return Results.Ok(new { configured = false, removed });
        });
    }

    public sealed record SetWebhookSecretRequest(string Secret);
}
