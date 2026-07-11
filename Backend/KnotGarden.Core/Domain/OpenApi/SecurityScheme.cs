namespace KnotGarden.Core.Domain.OpenApi;

public sealed record SecurityScheme(
    string Name,
    string Type,
    string? Scheme,
    string? In,
    string? ParamName,
    string? TokenUrl
);
