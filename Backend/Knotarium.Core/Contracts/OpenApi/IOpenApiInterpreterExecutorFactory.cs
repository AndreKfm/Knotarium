// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

namespace Knotarium.Core.Contracts.OpenApi;

/// <summary>
/// Builds the pre-compiled OpenAPI interpreter executor for interpreted (<c>openapi.*</c>) node
/// packages. This is the inversion seam that lets the Nodes slice run interpreted packages without
/// referencing the OpenApi feature slice: the concrete executor (and the stores it needs) live behind
/// this factory, which the host wires up from the OpenApi slice.
/// </summary>
public interface IOpenApiInterpreterExecutorFactory
{
    /// <summary>The reserved input key that carries the spec id the interpreter should run.</summary>
    string SpecIdInputKey { get; }

    /// <summary>Creates a fresh interpreter executor bound to the currently registered stores.</summary>
    INodeExecutor Create();
}
