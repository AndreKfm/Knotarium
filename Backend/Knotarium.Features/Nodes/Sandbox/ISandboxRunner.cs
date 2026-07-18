// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;

namespace Knotarium.Features.Nodes.Sandbox;

/// <summary>
/// Where user-authored node C# (inline code + custom package source) executes. The in-process
/// implementation preserves today's trusted-author behavior; the process implementation runs
/// the compiled executor in a pooled, OS-confined worker. Binary packages and built-in nodes
/// do not go through this seam.
/// </summary>
public interface ISandboxRunner
{
    /// <param name="cacheKey">Opaque compile-cache key (hash for inline code, type+version for packages).</param>
    /// <param name="source">Full executor class or bare script body (the compiler wraps the latter).</param>
    /// <param name="knownServices">Host services for constructor injection. Non-null entries force the
    /// in-process path when the executor's constructor needs them — services cannot cross the process
    /// boundary.</param>
    /// <param name="timeoutSeconds">The node's own execution budget; 0 = none declared (the process
    /// sandbox still applies its configured hard ceiling). In-process execution is bounded only by
    /// <paramref name="cancellationToken"/>, exactly as before.</param>
    Task<LegacyNodeResult> RunAsync(
        string cacheKey,
        string source,
        int timeoutSeconds,
        NodeExecutionContext context,
        IHttpClientFactory httpClientFactory,
        ICredentialAccessor credentialAccessor,
        ILogger logger,
        IReadOnlyDictionary<string, JsonElement>? extraInputs,
        IReadOnlyDictionary<Type, object?>? knownServices,
        CancellationToken cancellationToken);
}

/// <summary>Today's behavior: compile (cached), instantiate and run inside the backend process.</summary>
public sealed class InProcessSandboxRunner : ISandboxRunner
{
    private readonly CSharpScriptCompiler _compiler;

    public InProcessSandboxRunner(CSharpScriptCompiler compiler) => _compiler = compiler;

    public async Task<LegacyNodeResult> RunAsync(
        string cacheKey,
        string source,
        int timeoutSeconds,
        NodeExecutionContext context,
        IHttpClientFactory httpClientFactory,
        ICredentialAccessor credentialAccessor,
        ILogger logger,
        IReadOnlyDictionary<string, JsonElement>? extraInputs,
        IReadOnlyDictionary<Type, object?>? knownServices,
        CancellationToken cancellationToken)
    {
        var executorType = _compiler.GetOrCompile(cacheKey, source);
        var executor = _compiler.Instantiate(executorType, knownServices);
        return await _compiler.RunAsync(
            executor, context, httpClientFactory, credentialAccessor, logger, extraInputs, cancellationToken);
    }
}
