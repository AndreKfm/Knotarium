// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Knotarium.Core.Domain.OpenApi;

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
