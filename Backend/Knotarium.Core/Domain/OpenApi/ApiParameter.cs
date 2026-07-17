// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

namespace Knotarium.Core.Domain.OpenApi;

public sealed record ApiParameter(
    string Name,
    string In,
    bool Required,
    string? Description,
    string SchemaJson
);
