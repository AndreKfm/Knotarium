// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Runs a node whose executor was loaded from a prebuilt binary package (a *.dll discovered
/// on disk and registered in <see cref="Knotarium.NodeRuntime.INodeExecutorRegistry"/>),
/// as opposed to <see cref="DynamicCustomNodeTask"/> which Roslyn-compiles C# source from the
/// database. Both ultimately funnel through the same run/normalize path so behaviour is
/// identical regardless of how the executor was obtained.
/// </summary>
public sealed class BinaryPackageNodeTask : INodeTask
{
    private readonly INodeExecutor _executor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICredentialAccessor _credentialAccessor;
    private readonly ILogger _logger;
    private readonly IExternalSignalProvider? _externalSignals;
    private readonly CSharpScriptCompiler _runner = new();

    public BinaryPackageNodeTask(
        INodeExecutor executor,
        IHttpClientFactory httpClientFactory,
        ICredentialAccessor credentialAccessor,
        ILogger logger,
        IExternalSignalProvider? externalSignals = null)
    {
        _executor = executor;
        _httpClientFactory = httpClientFactory;
        _credentialAccessor = credentialAccessor;
        _logger = logger;
        _externalSignals = externalSignals;
    }

    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            // RunAsync only uses the compiler instance as a host for the shared run/normalize
            // logic — no compilation happens here; the executor is already instantiated.
            return await _runner.RunAsync(
                _executor, context, _httpClientFactory, _credentialAccessor, _logger, extraInputs: null, cancellationToken, _externalSignals);
        }
        catch (System.Exception ex)
        {
            return new LegacyNodeResult.Failure($"Binary package node execution threw an exception: {ex.Message}");
        }
    }
}
