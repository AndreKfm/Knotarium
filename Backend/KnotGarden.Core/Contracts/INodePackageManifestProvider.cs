using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Domain;

namespace KnotGarden.Core.Contracts;

public interface INodePackageManifestProvider
{
    Task<NodePackageManifest?> GetManifestAsync(NodePackageId packageId, CancellationToken cancellationToken = default);
}
