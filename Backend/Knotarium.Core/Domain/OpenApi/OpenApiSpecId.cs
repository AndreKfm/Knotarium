// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

namespace Knotarium.Core.Domain.OpenApi;

public readonly record struct OpenApiSpecId(string Value)
{
    public override string ToString() => Value;
}
