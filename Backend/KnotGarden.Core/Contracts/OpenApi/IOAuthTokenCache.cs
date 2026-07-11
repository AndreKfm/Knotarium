using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KnotGarden.Core.Contracts.OpenApi;

public interface IOAuthTokenCache
{
    /// <summary>Returns a valid bearer token, fetching or refreshing from the token endpoint as needed.</summary>
    Task<string> GetTokenAsync(
        string cacheKey,
        string tokenUrl,
        string clientId,
        string clientSecret,
        IReadOnlyList<string> scopes,
        CancellationToken ct = default);

    /// <summary>Removes the cached token so the next call fetches a fresh one.</summary>
    void Invalidate(string cacheKey);
}
