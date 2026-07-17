// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Knotarium.Core.Domain.OpenApi;

public sealed record ApiRequestBody(
    bool Required,
    IReadOnlyList<string> MediaTypes,
    string SchemaJson
);
