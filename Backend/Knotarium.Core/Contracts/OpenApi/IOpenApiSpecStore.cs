// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain.OpenApi;

namespace Knotarium.Core.Contracts.OpenApi;

public interface IOpenApiSpecStore
{
    Task<ImportedSpec> SaveAsync(ParsedSpec spec, CancellationToken ct = default);

    Task<(ImportedSpec Spec, ParsedSpec Full)?> GetLatestAsync(OpenApiSpecId id, CancellationToken ct = default);

    Task<(ImportedSpec Spec, ParsedSpec Full)?> GetVersionAsync(OpenApiSpecId id, int versionNumber, CancellationToken ct = default);

    Task<IReadOnlyList<ImportedSpec>> ListAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ImportedSpec>> GetVersionsAsync(OpenApiSpecId id, CancellationToken ct = default);

    Task<ApiOperation?> GetOperationAsync(OpenApiSpecId id, string operationId, CancellationToken ct = default);

    Task<bool> DeleteAsync(OpenApiSpecId id, CancellationToken ct = default);
}
