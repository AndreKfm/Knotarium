// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Knotarium.Infrastructure.Persistence.OpenApi;

public class OpenApiSpecVersionEntity
{
    public Guid RowId { get; set; }
    public string SpecId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public string OriginalFormat { get; set; } = string.Empty;
    public string ParsedSpecJson { get; set; } = string.Empty;
    public DateTimeOffset ImportedAtUtc { get; set; }
    public OpenApiSpecEntity Spec { get; set; } = null!;
}
