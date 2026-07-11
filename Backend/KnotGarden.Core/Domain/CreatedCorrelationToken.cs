using System;

namespace KnotGarden.Core.Domain;

public sealed record CreatedCorrelationToken(
    Guid Id,
    string RawToken,
    DateTimeOffset ExpiresAtUtc
);
