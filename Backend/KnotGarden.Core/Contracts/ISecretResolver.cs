using System.Threading;
using System.Threading.Tasks;

namespace KnotGarden.Core.Contracts;

public interface ISecretResolver
{
    Task<string?> ResolveAsync(string secretRef, CancellationToken cancellationToken = default);
}
