// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Knotarium.Core.Domain.OpenApi;

public sealed record ApiOperation(
    string OperationId,
    string Method,
    string PathTemplate,
    string? Summary,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ApiParameter> Parameters,
    ApiRequestBody? RequestBody,
    IReadOnlyList<string> SecurityRefs
);
