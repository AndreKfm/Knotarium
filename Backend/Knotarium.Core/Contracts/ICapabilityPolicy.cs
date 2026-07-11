using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts;

/// <summary>
/// Guard consulted by privileged nodes (inline code, database query) before they run. Returns whether a
/// given capability tag is enabled by the instance <see cref="Domain.CapabilityPolicy"/>. Secure by
/// default: an unconfigured instance reports every switchable capability as disabled.
/// </summary>
public interface ICapabilityPolicy
{
    Task<bool> IsEnabledAsync(string capability, CancellationToken cancellationToken = default);
}
