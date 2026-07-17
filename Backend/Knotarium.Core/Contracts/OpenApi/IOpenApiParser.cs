// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts.OpenApi;

public interface IOpenApiParser
{
    /// <summary>Parses JSON or YAML bytes into a normalized ParsedSpec.</summary>
    /// <exception cref="Knotarium.Core.Exceptions.OpenApiParseException">On parse error or external $ref detected.</exception>
    Task<ParsedSpec> ParseAsync(ReadOnlyMemory<byte> content, CancellationToken ct = default);
}
