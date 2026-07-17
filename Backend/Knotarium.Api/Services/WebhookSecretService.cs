// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;

namespace Knotarium.Api.Services;

/// <summary>Outcome of checking a presented webhook secret against a workflow's stored secret.</summary>
public enum WebhookSecretCheck
{
    /// <summary>No secret is configured for this workflow — the anonymous trigger stays open (legacy behavior).</summary>
    NotConfigured,

    /// <summary>A secret is configured and the presented value matched.</summary>
    Valid,

    /// <summary>A secret is configured but the presented value was missing or wrong.</summary>
    Invalid,
}

/// <summary>
/// Optional per-workflow shared secret guarding the anonymous webhook trigger (<c>POST /api/executions</c>).
/// The secret is stored only as a SHA-256 hash in the <see cref="AppSetting"/> key/value table (never in
/// plaintext, never returned to clients — the same posture as resume tokens and passwords). A caller proves
/// possession by presenting the raw secret in the <c>X-Knotarium-Webhook-Secret</c> header; verification is a
/// constant-time hash comparison. Workflows without a configured secret keep working unauthenticated, so
/// enabling a secret is opt-in and backward compatible.
/// </summary>
public sealed class WebhookSecretService
{
    private readonly AppDbContext _db;

    public WebhookSecretService(AppDbContext db) => _db = db;

    /// <summary>Header a webhook caller sets to present the raw secret.</summary>
    public const string HeaderName = "X-Knotarium-Webhook-Secret";

    private static string SettingKey(string workflowId) => $"webhook.secret.{workflowId}";

    public async Task<bool> HasSecretAsync(string workflowId, CancellationToken ct = default)
    {
        var key = SettingKey(workflowId);
        return await _db.AppSettings.AsNoTracking()
            .AnyAsync(s => s.Key == key && s.Value != null && s.Value != string.Empty, ct)
            .ConfigureAwait(false);
    }

    public async Task SetAsync(string workflowId, string secret, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("Webhook secret must not be empty.", nameof(secret));
        }

        var key = SettingKey(workflowId);
        var hash = Hash(secret);
        var existing = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
        if (existing is null)
        {
            _db.AppSettings.Add(new AppSetting { Key = key, Value = hash });
        }
        else
        {
            existing.Value = hash;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes any configured secret; returns true if one existed.</summary>
    public async Task<bool> ClearAsync(string workflowId, CancellationToken ct = default)
    {
        var key = SettingKey(workflowId);
        var removed = await _db.AppSettings.Where(s => s.Key == key).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return removed > 0;
    }

    public async Task<WebhookSecretCheck> VerifyAsync(string workflowId, string? presented, CancellationToken ct = default)
    {
        var key = SettingKey(workflowId);
        var stored = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(stored))
        {
            return WebhookSecretCheck.NotConfigured;
        }
        if (string.IsNullOrEmpty(presented))
        {
            return WebhookSecretCheck.Invalid;
        }

        var presentedHash = Hash(presented);
        var match = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(stored),
            Encoding.ASCII.GetBytes(presentedHash));
        return match ? WebhookSecretCheck.Valid : WebhookSecretCheck.Invalid;
    }

    private static string Hash(string secret) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
