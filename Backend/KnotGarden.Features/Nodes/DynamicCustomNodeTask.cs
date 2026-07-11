using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Contracts.OpenApi;
using KnotGarden.Core.Domain;
using KnotGarden.NodeRuntime;

namespace KnotGarden.Features.Nodes;

public class DynamicCustomNodeTask : INodeTask
{
    private readonly string _nodeType;
    private readonly INodePackageReadStore _packageStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICredentialAccessor _credentialAccessor;
    private readonly ILogger _logger;
    private readonly IOpenApiSpecStore? _openApiSpecStore;
    private readonly IServerConfigStore? _serverConfigStore;
    private readonly IOAuthTokenCache? _oAuthTokenCache;
    private readonly IOpenApiInterpreterExecutorFactory? _interpreterFactory;
    private readonly CSharpScriptCompiler _compiler = new();

    public DynamicCustomNodeTask(
        string nodeType,
        INodePackageReadStore packageStore,
        IHttpClientFactory httpClientFactory,
        ICredentialAccessor credentialAccessor,
        ILogger logger,
        IOpenApiSpecStore? openApiSpecStore = null,
        IServerConfigStore? serverConfigStore = null,
        IOAuthTokenCache? oAuthTokenCache = null,
        IOpenApiInterpreterExecutorFactory? interpreterFactory = null)
    {
        _nodeType            = nodeType;
        _packageStore        = packageStore;
        _httpClientFactory   = httpClientFactory;
        _credentialAccessor  = credentialAccessor;
        _logger              = logger;
        _openApiSpecStore    = openApiSpecStore;
        _serverConfigStore   = serverConfigStore;
        _oAuthTokenCache     = oAuthTokenCache;
        _interpreterFactory  = interpreterFactory;
    }

    public async Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        // 1. Fetch the package's latest version via the read seam.
        var packageId = new NodePackageId(_nodeType);
        var latestVersion = await _packageStore.GetLatestVersionAsync(packageId, cancellationToken);

        if (latestVersion == null)
        {
            return new LegacyNodeResult.Failure($"Custom package '{_nodeType}' not found in database.");
        }

        // Deserialize manifest
        NodePackageManifest manifest;
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            manifest = JsonSerializer.Deserialize<NodePackageManifest>(latestVersion.ManifestJson, options)
                ?? throw new InvalidOperationException("Failed to deserialize manifest.");
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"Failed to parse package manifest: {ex.Message}");
        }

        // 2. Instantiate INodeExecutor
        INodeExecutor executor;
        IReadOnlyDictionary<string, JsonElement>? extraInputs = null;
        if (manifest.Tier == NodeTier.Declarative)
        {
            executor = new DeclarativeExecutor(manifest);
        }
        else if (manifest.Tier == NodeTier.Interpreted)
        {
            // All openapi.* nodes route to the single pre-compiled interpreter — no Roslyn.
            // The interpreter lives in the OpenApi slice; the Nodes slice reaches it only through the
            // Core factory seam. The spec to run is implicit in the package identity (type minus the
            // "openapi." prefix); we surface it to the executor via the reserved __specId input below.
            if (_interpreterFactory is null)
            {
                return new LegacyNodeResult.Failure(
                    $"Interpreted node '{_nodeType}' requires the OpenAPI interpreter to be registered.");
            }
            executor = _interpreterFactory.Create();
            var interpretedSpecId = _nodeType.StartsWith("openapi.", StringComparison.Ordinal)
                ? _nodeType["openapi.".Length..]
                : _nodeType;
            extraInputs = new Dictionary<string, JsonElement>
            {
                [_interpreterFactory.SpecIdInputKey] = JsonSerializer.SerializeToElement(interpretedSpecId)
            };
        }
        else
        {
            // Compiled tier: compile (or fetch from cache) via the shared Roslyn compiler.
            try
            {
                if (string.IsNullOrWhiteSpace(latestVersion.Source))
                {
                    return new LegacyNodeResult.Failure("Custom node executor source code is missing.");
                }

                var cacheKey = $"{_nodeType}_{latestVersion.Version}_{latestVersion.CreatedAt.Ticks}";
                _logger.LogInformation("Compiling custom C# node '{NodeType}'...", _nodeType);
                var executorType = _compiler.GetOrCompile(cacheKey, latestVersion.Source);

                var knownServices = new Dictionary<Type, object?>
                {
                    [typeof(IOpenApiSpecStore)]  = _openApiSpecStore,
                    [typeof(IServerConfigStore)] = _serverConfigStore,
                    [typeof(IOAuthTokenCache)]   = _oAuthTokenCache,
                };
                executor = _compiler.Instantiate(executorType, knownServices);
            }
            catch (ScriptCompilationException ex)
            {
                return new LegacyNodeResult.Failure($"C# Compilation of '{_nodeType}' failed:\n{ex.Message}");
            }
            catch (Exception ex)
            {
                return new LegacyNodeResult.Failure($"Failed to load dynamic C# executor for '{_nodeType}': {ex.Message}");
            }
        }

        // 3. Run executor + normalize result
        try
        {
            return await _compiler.RunAsync(
                executor, context, _httpClientFactory, _credentialAccessor, _logger, extraInputs, cancellationToken);
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure($"Custom node execution threw an exception: {ex.Message}");
        }
    }
}
