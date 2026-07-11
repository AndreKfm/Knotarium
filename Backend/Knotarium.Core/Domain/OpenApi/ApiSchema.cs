namespace Knotarium.Core.Domain.OpenApi;

public sealed record ApiSchema(
    string Name,
    string? Description,
    string SchemaJson
);
