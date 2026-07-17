// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Knotarium.Core.Domain.OpenApi;

namespace Knotarium.Core.Contracts.OpenApi;

public sealed record ParsedSpec(
    ImportedSpec Metadata,
    IReadOnlyList<ApiOperation> Operations,
    IReadOnlyList<ApiSchema> Schemas,
    IReadOnlyList<SecurityScheme> SecuritySchemes
);
