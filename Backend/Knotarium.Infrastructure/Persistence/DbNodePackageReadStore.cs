// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Infrastructure.Persistence;

/// <summary>
/// EF-backed <see cref="INodePackageReadStore"/> over the shared <see cref="AppDbContext"/>. Owns the
/// runtime read of deployed node packages (existence check + latest-version payload) so the Nodes slice
/// resolves and runs custom packages without binding the concrete DbContext.
/// </summary>
public sealed class DbNodePackageReadStore : INodePackageReadStore
{
    private readonly AppDbContext _dbContext;

    public DbNodePackageReadStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public bool Exists(NodePackageId id) => _dbContext.NodePackages.Any(p => p.Id == id);

    public async Task<NodePackageVersion?> GetLatestVersionAsync(NodePackageId id, CancellationToken cancellationToken = default)
    {
        var package = await _dbContext.NodePackages
            .Include(p => p.Versions)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        return package?.Versions?
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefault();
    }
}
