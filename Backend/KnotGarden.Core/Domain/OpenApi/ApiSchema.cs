namespace KnotGarden.Core.Domain.OpenApi;

public sealed record ApiSchema(
    string Name,
    string? Description,
    string SchemaJson
);
