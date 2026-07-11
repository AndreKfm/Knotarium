using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Contracts.OpenApi;
using KnotGarden.Core.Domain;
using KnotGarden.Core.Domain.OpenApi;

namespace KnotGarden.Features.OpenApi;

/// <summary>
/// Deletes an imported OpenAPI spec together with the node package it generated, so the
/// operation node disappears from the palette and can never reference a now-deleted spec.
/// Mirror of <see cref="ImportOpenApiSpecHandler"/>, which creates both the spec and the package.
/// </summary>
public sealed class DeleteOpenApiSpecHandler(IOpenApiSpecStore specStore, INodePackageStore packageStore)
{
    public async Task<bool> HandleAsync(string specId, CancellationToken ct = default)
    {
        var specDeleted = await specStore.DeleteAsync(new OpenApiSpecId(specId), ct);

        // The package id is deterministic from the spec id (see OpenApiNodeGenerator.BuildPackageId).
        // Delete it independently of the spec so a previously-orphaned package is also cleaned up.
        var packageId = NodePackageId.Create(OpenApiNodeGenerator.BuildPackageId(specId));
        var packageDeleted = await packageStore.DeleteAsync(packageId, ct);

        // NoContent if anything was removed; the endpoint maps false → 404.
        return specDeleted || packageDeleted;
    }
}
