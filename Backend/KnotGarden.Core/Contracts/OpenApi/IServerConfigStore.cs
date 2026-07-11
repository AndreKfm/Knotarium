using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Domain.OpenApi;

namespace KnotGarden.Core.Contracts.OpenApi;

public interface IServerConfigStore
{
    Task<ServerConfigInfo> CreateAsync(ServerConfigInfo config, CancellationToken ct = default);

    Task<ServerConfigInfo> UpdateAsync(ServerConfigInfo config, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    Task<ServerConfigInfo?> GetAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<ServerConfigInfo>> ListAsync(CancellationToken ct = default);
}
