using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Secure fallback provider: denies all file access. Registered as the default so that any host which wires
/// the built-in nodes but not the settings-backed policy store still fails closed rather than open. The real,
/// persistence-backed provider (registered by the settings slice) overrides this.
/// </summary>
public sealed class DeniedFileAccessPolicyProvider : IFileAccessPolicyProvider
{
    public Task<FileAccessPolicy> GetPolicyAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(FileAccessPolicy.Denied);
}
