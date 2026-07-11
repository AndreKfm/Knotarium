using System.Collections.Generic;

namespace KnotGarden.Core.Domain.OpenApi;

public sealed record ApiOperation(
    string OperationId,
    string Method,
    string PathTemplate,
    string? Summary,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ApiParameter> Parameters,
    ApiRequestBody? RequestBody,
    IReadOnlyList<string> SecurityRefs
);
