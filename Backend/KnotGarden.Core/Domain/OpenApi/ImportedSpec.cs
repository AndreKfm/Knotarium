using System;
using System.Collections.Generic;

namespace KnotGarden.Core.Domain.OpenApi;

public sealed record ImportedSpec(
    OpenApiSpecId Id,
    string Title,
    string Version,
    string OriginalFormat,
    IReadOnlyList<string> DefaultServers,
    IReadOnlyList<string> Tags,
    DateTimeOffset ImportedAtUtc,
    int SpecVersionNumber
);
