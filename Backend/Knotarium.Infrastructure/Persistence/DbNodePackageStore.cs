// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Infrastructure.Persistence;

/// <summary>
/// EF-backed <see cref="INodePackageStore"/> over the shared <see cref="AppDbContext"/>. Owns the
/// upsert/delete against the <c>NodePackages</c>/<c>NodePackageVersions</c> tables so feature slices
/// (the OpenAPI import/delete handlers) never bind the concrete DbContext.
/// </summary>
public sealed class DbNodePackageStore : INodePackageStore
{
    private readonly AppDbContext _dbContext;

    public DbNodePackageStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> DeleteAsync(NodePackageId id, CancellationToken cancellationToken = default)
    {
        var package = await _dbContext.NodePackages
            .Include(p => p.Versions)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (package is null)
        {
            return false;
        }

        _dbContext.RemoveRange(package.Versions);
        _dbContext.Remove(package);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task UpsertGeneratedPackageAsync(
        NodePackageId id,
        string displayName,
        string category,
        NodePackageVersion version,
        CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.NodePackages
            .Include(p => p.Versions)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (existing is null)
        {
            existing = new NodePackage
            {
                Id = id,
                DisplayName = displayName,
                Category = category
            };
            _dbContext.NodePackages.Add(existing);
        }
        else
        {
            existing.DisplayName = displayName;
        }

        existing.Versions.Add(version);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
