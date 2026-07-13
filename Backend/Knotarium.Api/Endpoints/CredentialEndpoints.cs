using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Api;

/// <summary>
/// Credential CRUD. Secret values are encrypted at rest via <see cref="ICredentialCipher"/> and
/// never leave the server — every response masks <c>EncryptedValue</c> to "***".
/// </summary>
public static class CredentialEndpoints
{
    public static void MapCredentialEndpoints(this WebApplication app)
    {
        app.MapGet("/api/credentials", async (AppDbContext db) =>
        {
            var list = await db.Credentials.ToListAsync();
            var maskedList = list.Select(c => new Credential
            {
                Id = c.Id,
                Name = c.Name,
                EncryptedValue = "***",
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt
            }).ToList();
            return Results.Ok(maskedList);
        });

        app.MapPost("/api/credentials", async (CreateCredentialRequest request, AppDbContext db, ICredentialCipher cipher, Knotarium.Api.Services.Auth.AuthOptions auth, System.Security.Claims.ClaimsPrincipal user) =>
        {
            // Writing a credential (a plaintext secret at the boundary) is an admin action.
            if (auth.RequireAdmin(user) is { } denied) return denied;
            if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Value))
            {
                return Results.BadRequest(new { message = "Id, Name, and Value are required" });
            }

            var existing = await db.Credentials.FindAsync(request.Id);
            if (existing != null)
            {
                existing.Name = request.Name;
                existing.EncryptedValue = cipher.Encrypt(request.Value);
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();

                return Results.Ok(new Credential
                {
                    Id = existing.Id,
                    Name = existing.Name,
                    EncryptedValue = "***",
                    CreatedAt = existing.CreatedAt,
                    UpdatedAt = existing.UpdatedAt
                });
            }
            else
            {
                var cred = new Credential
                {
                    Id = request.Id,
                    Name = request.Name,
                    EncryptedValue = cipher.Encrypt(request.Value),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                db.Credentials.Add(cred);
                await db.SaveChangesAsync();

                return Results.Created($"/api/credentials/{cred.Id}", new Credential
                {
                    Id = cred.Id,
                    Name = cred.Name,
                    EncryptedValue = "***",
                    CreatedAt = cred.CreatedAt,
                    UpdatedAt = cred.UpdatedAt
                });
            }
        });

        app.MapDelete("/api/credentials/{id}", async (string id, AppDbContext db, Knotarium.Api.Services.Auth.AuthOptions auth, System.Security.Claims.ClaimsPrincipal user) =>
        {
            if (auth.RequireAdmin(user) is { } denied) return denied;
            var cred = await db.Credentials.FindAsync(id);
            if (cred == null)
            {
                return Results.NotFound();
            }

            db.Credentials.Remove(cred);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
