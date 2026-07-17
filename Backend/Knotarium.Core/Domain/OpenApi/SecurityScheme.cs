// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

namespace Knotarium.Core.Domain.OpenApi;

public sealed record SecurityScheme(
    string Name,
    string Type,
    string? Scheme,
    string? In,
    string? ParamName,
    string? TokenUrl
);
