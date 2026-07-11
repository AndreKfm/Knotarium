namespace KnotGarden.Core.Domain.OpenApi;

public sealed record ApiParameter(
    string Name,
    string In,
    bool Required,
    string? Description,
    string SchemaJson
);
