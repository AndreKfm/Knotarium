// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Write-side seam over the deployed node-package store, so feature slices can persist and delete
/// generated packages (e.g. the OpenAPI import/delete handlers) without binding the concrete
/// <c>AppDbContext</c>. Read-side lookups live behind <see cref="INodePackageManifestProvider"/> and
/// <see cref="INodePackageCatalogProvider"/>; the EF adapter lives in the host/Infrastructure.
/// </summary>
public interface INodePackageStore
{
    /// <summary>Deletes the package with the given id together with all its versions.</summary>
    /// <returns><c>true</c> if a package existed and was removed; <c>false</c> if none matched.</returns>
    Task<bool> DeleteAsync(NodePackageId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts a generated package: creates it with the given display name/category when absent
    /// (otherwise refreshes the display name), then appends <paramref name="version"/>.
    /// </summary>
    Task UpsertGeneratedPackageAsync(
        NodePackageId id,
        string displayName,
        string category,
        NodePackageVersion version,
        CancellationToken cancellationToken = default);
}
