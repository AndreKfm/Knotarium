// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain.OpenApi;

namespace Knotarium.Core.Contracts.OpenApi;

public interface IServerConfigStore
{
    Task<ServerConfigInfo> CreateAsync(ServerConfigInfo config, CancellationToken ct = default);

    Task<ServerConfigInfo> UpdateAsync(ServerConfigInfo config, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    Task<ServerConfigInfo?> GetAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<ServerConfigInfo>> ListAsync(CancellationToken ct = default);
}
