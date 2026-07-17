// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Infrastructure.Persistence;

/// <summary>
/// EF-backed <see cref="ISettingsStore"/> over the shared <see cref="AppDbContext"/>. Owns the actual
/// read/upsert against the <c>AppSettings</c> table so feature slices (Settings, and via it Ai and the
/// error-workflow worker) never bind the concrete DbContext.
/// </summary>
public sealed class DbSettingsStore : ISettingsStore
{
    private readonly AppDbContext _dbContext;

    public DbSettingsStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        return row?.Value;
    }

    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is null)
        {
            await _dbContext.AppSettings.AddAsync(new AppSetting { Key = key, Value = value }, cancellationToken);
        }
        else
        {
            row.Value = value;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
