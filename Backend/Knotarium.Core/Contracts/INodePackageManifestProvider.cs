using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

public interface INodePackageManifestProvider
{
    Task<NodePackageManifest?> GetManifestAsync(NodePackageId packageId, CancellationToken cancellationToken = default);
}
