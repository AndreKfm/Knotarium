using System.Collections.Generic;
using KnotGarden.Core.Domain.OpenApi;

namespace KnotGarden.Core.Contracts.OpenApi;

public sealed record ParsedSpec(
    ImportedSpec Metadata,
    IReadOnlyList<ApiOperation> Operations,
    IReadOnlyList<ApiSchema> Schemas,
    IReadOnlyList<SecurityScheme> SecuritySchemes
);
