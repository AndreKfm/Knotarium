// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;

namespace Knotarium.Features.Execution;

/// <summary>
/// Resolves node manifests for the execution engine, filling in the engine's conservative
/// defaults: an unspecified side-effect kind is treated as non-idempotent and a missing retry
/// policy becomes the default policy.
/// </summary>
internal sealed class NodeManifestSource
{
    private readonly WorkflowCompiler _compiler;

    public NodeManifestSource(WorkflowCompiler compiler)
    {
        _compiler = compiler;
    }

    public async Task<NodePackageManifest?> GetManifestAsync(string nodeType, CancellationToken cancellationToken)
    {
        var manifest = await _compiler.ManifestProvider.GetManifestAsync(new NodePackageId(nodeType), cancellationToken);
        if (manifest == null)
        {
            return null;
        }

        return manifest with
        {
            SideEffectKind = manifest.SideEffectKind ?? NodeSideEffectKind.NonIdempotentSideEffect,
            RetryPolicy = manifest.RetryPolicy ?? new RetryPolicy()
        };
    }
}
