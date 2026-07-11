using System;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

public interface ICorrelationTokenService
{
    Task<CreatedCorrelationToken> CreateTokenAsync(
        ExecutionInstanceId executionInstanceId, 
        NodeId nodeId, 
        TimeSpan ttl, 
        CancellationToken cancellationToken = default);

    Task<CorrelationToken?> VerifyAndClaimAsync(string rawToken, CancellationToken cancellationToken = default);
}
