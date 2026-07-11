using System.Collections.Generic;

namespace KnotGarden.Core.Domain.OpenApi;

public sealed record ApiRequestBody(
    bool Required,
    IReadOnlyList<string> MediaTypes,
    string SchemaJson
);
