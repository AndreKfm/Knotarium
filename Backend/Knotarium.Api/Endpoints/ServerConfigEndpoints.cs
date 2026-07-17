// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Knotarium.Core.Contracts.OpenApi;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Api;

/// <summary>
/// Server-configuration CRUD (named API base URLs + auth scheme + optional credential ref) used by
/// the OpenAPI/HTTP nodes. Persisted via <see cref="IServerConfigStore"/>; credential refs are
/// validated against the DB on write.
/// </summary>
public static class ServerConfigEndpoints
{
    public static void MapServerConfigEndpoints(this WebApplication app)
    {
        app.MapGet("/api/server-configs", async (IServerConfigStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)));

        app.MapGet("/api/server-configs/{id}", async (string id, IServerConfigStore store, CancellationToken ct) =>
        {
            var config = await store.GetAsync(id, ct);
            return config is null ? Results.NotFound(new { message = $"ServerConfig '{id}' not found." }) : Results.Ok(config);
        });

        app.MapPost("/api/server-configs", async (CreateServerConfigRequest req, IServerConfigStore store, AppDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { message = "Name is required." });
            if (string.IsNullOrWhiteSpace(req.BaseUrl))
                return Results.BadRequest(new { message = "BaseUrl is required." });

            if (!string.IsNullOrWhiteSpace(req.CredentialRef))
            {
                var credExists = await db.Credentials.AnyAsync(c => c.Id == req.CredentialRef, ct);
                if (!credExists)
                    return Results.BadRequest(new { message = $"CredentialRef '{req.CredentialRef}' not found." });
            }

            var now = DateTimeOffset.UtcNow;
            var config = new Knotarium.Core.Domain.OpenApi.ServerConfigInfo(
                Guid.NewGuid().ToString("N"),
                req.Name,
                req.BaseUrl,
                req.ServerVariables ?? new Dictionary<string, string>(),
                req.SecuritySchemeType ?? "none",
                req.CredentialRef,
                now, now,
                req.AllowInsecureCertificate);

            var created = await store.CreateAsync(config, ct);
            return Results.Created($"/api/server-configs/{created.Id}", created);
        });

        app.MapPut("/api/server-configs/{id}", async (string id, UpdateServerConfigRequest req, IServerConfigStore store, AppDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { message = "Name is required." });
            if (string.IsNullOrWhiteSpace(req.BaseUrl))
                return Results.BadRequest(new { message = "BaseUrl is required." });

            var existing = await store.GetAsync(id, ct);
            if (existing is null)
                return Results.NotFound(new { message = $"ServerConfig '{id}' not found." });

            if (!string.IsNullOrWhiteSpace(req.CredentialRef))
            {
                var credExists = await db.Credentials.AnyAsync(c => c.Id == req.CredentialRef, ct);
                if (!credExists)
                    return Results.BadRequest(new { message = $"CredentialRef '{req.CredentialRef}' not found." });
            }

            var updated = new Knotarium.Core.Domain.OpenApi.ServerConfigInfo(
                id,
                req.Name,
                req.BaseUrl,
                req.ServerVariables ?? new Dictionary<string, string>(),
                req.SecuritySchemeType ?? "none",
                req.CredentialRef,
                existing.CreatedAt,
                DateTimeOffset.UtcNow,
                req.AllowInsecureCertificate);

            await store.UpdateAsync(updated, ct);
            return Results.Ok(updated);
        });

        app.MapDelete("/api/server-configs/{id}", async (string id, IServerConfigStore store, CancellationToken ct) =>
        {
            var existing = await store.GetAsync(id, ct);
            if (existing is null)
                return Results.NotFound(new { message = $"ServerConfig '{id}' not found." });
            await store.DeleteAsync(id, ct);
            return Results.NoContent();
        });
    }
}
