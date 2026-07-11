using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Supplies the full node-package catalog (built-ins + every deployed binary/DB package). Distinct from
/// <see cref="INodePackageManifestProvider"/> (single-manifest lookup by id): this is the "list everything"
/// seam the AI generation runner needs, so the runner can live in <c>Knotarium.Features</c> without
/// depending on the host-owned <c>DbNodePackageManifestProvider</c>.
/// </summary>
public interface INodePackageCatalogProvider
{
    /// <summary>Returns the manifests for every currently deployed node package.</summary>
    Task<IReadOnlyList<NodePackageManifest>> GetAllManifestsAsync(CancellationToken cancellationToken = default);
}
