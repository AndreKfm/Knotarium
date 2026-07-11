using System;

namespace Knotarium.Core.Domain;

public sealed record CreatedCorrelationToken(
    Guid Id,
    string RawToken,
    DateTimeOffset ExpiresAtUtc
);
