using System;
using System.Collections.Generic;

namespace Knotarium.Infrastructure.Persistence.OpenApi;

public class OpenApiSpecEntity
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public List<OpenApiSpecVersionEntity> Versions { get; set; } = new();
}
