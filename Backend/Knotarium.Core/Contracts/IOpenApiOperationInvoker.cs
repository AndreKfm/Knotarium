// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts;

/// <summary>Minimal seam over the OpenAPI interpreter for polling: run an operation, get its response.</summary>
public interface IOpenApiOperationInvoker
{
    Task<OpenApiPollResponse> InvokeAsync(
        string serverConfigId, string operationId, string? specVersion, CancellationToken cancellationToken);
}

/// <summary>Raw response from an OpenAPI operation poll.</summary>
public sealed record OpenApiPollResponse(string Body, string? ETag, string? LastModified);
