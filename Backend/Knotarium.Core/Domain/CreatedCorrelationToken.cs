// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Knotarium.Core.Domain;

public sealed record CreatedCorrelationToken(
    Guid Id,
    string RawToken,
    DateTimeOffset ExpiresAtUtc
);
