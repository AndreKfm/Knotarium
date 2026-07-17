// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Read-side seam over deployed node packages for the runtime, letting the Nodes slice resolve and run
/// a custom package without binding the concrete <c>AppDbContext</c>. Complements the write-side
/// <see cref="INodePackageStore"/> and the manifest-only <see cref="INodePackageManifestProvider"/> /
/// <see cref="INodePackageCatalogProvider"/> — this one also carries the version payload (source) the
/// dynamic executor needs. The EF adapter lives in Infrastructure.
/// </summary>
public interface INodePackageReadStore
{
    /// <summary>
    /// True if a package with this id exists. Synchronous because the node-task registry's
    /// <c>GetTask</c> is a synchronous factory — avoids sync-over-async on that path.
    /// </summary>
    bool Exists(NodePackageId id);

    /// <summary>The latest version (by <c>CreatedAt</c>) of the package, or null when the package has none.</summary>
    Task<NodePackageVersion?> GetLatestVersionAsync(NodePackageId id, CancellationToken cancellationToken = default);
}
