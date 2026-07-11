using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Domain.OpenApi;

namespace KnotGarden.Core.Contracts.OpenApi;

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
