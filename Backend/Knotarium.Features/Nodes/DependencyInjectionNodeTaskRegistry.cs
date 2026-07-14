using System;
using System.Linq;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.OpenApi;
using Knotarium.Core.Domain;

namespace Knotarium.Features.Nodes;

public class DependencyInjectionNodeTaskRegistry : INodeTaskRegistry
{
    private readonly IServiceProvider _serviceProvider;

    public DependencyInjectionNodeTaskRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    private ILogger Logger =>
        _serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<DependencyInjectionNodeTaskRegistry>()
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DependencyInjectionNodeTaskRegistry>.Instance;

    // Resolve a required collaborator, logging a warning if it is missing and a stand-in is used. The
    // stand-ins exist only for barebone test hosts; in a real host these are always registered, so a
    // fallback firing there is a misconfiguration worth surfacing (e.g. the HTTP stand-in bypasses the
    // egress policy) rather than swallowing silently.
    private T ResolveOrFallback<T>(Func<T> fallback, string what) where T : class
    {
        var resolved = _serviceProvider.GetService<T>();
        if (resolved is not null)
        {
            return resolved;
        }
        Logger.LogWarning("Node-task registry falling back to a stand-in {What}; expected it to be registered.", what);
        return fallback();
    }

    public INodeTask? GetTask(string nodeType)
    {
        var type = nodeType.ToLowerInvariant() switch
        {
            "start" => typeof(StartNodeTask),
            "condition" => typeof(ConditionNodeTask),
            "setvariable" => typeof(SetVariableNodeTask),
            "setvariables" => typeof(SetVariablesNodeTask),
            "httprequest" => typeof(HttpRequestNodeTask),
            "delay" => typeof(DelayNodeTask),
            "log" => typeof(LogNodeTask),
            "forloop" => typeof(ForLoopNodeTask),
            "join" => typeof(JoinNodeTask),
            "inlinecode" => typeof(InlineCodeNodeTask),
            "sendnotification" => typeof(SendNotificationNodeTask),
            "resourcepicker" => typeof(ResourcePickerNodeTask),
            "dbquery" => typeof(DbQueryNodeTask),
            "fileread" => typeof(FileReadNodeTask),
            "filewrite" => typeof(FileWriteNodeTask),
            "smtpsend" => typeof(SmtpSendNodeTask),
            "imapfetch" => typeof(ImapFetchNodeTask),
            "mqpublish" => typeof(MqPublishNodeTask),
            "aiprompt" => typeof(AiPromptNodeTask),
            "aiclassify" => typeof(AiClassifyNodeTask),
            "end" => typeof(EndNodeTask),
            _ => null
        };

        if (type != null)
        {
            return _serviceProvider.GetService(type) as INodeTask;
        }

        // Try to resolve a prebuilt binary package (a *.dll loaded from disk into the registry).
        // Checked before the database so binary packages take precedence over any same-id source
        // package, and so it works even when no package store / database is available.
        try
        {
            var executorRegistry = _serviceProvider.GetService<Knotarium.NodeRuntime.INodeExecutorRegistry>();
            var registered = executorRegistry?.GetLatest(new NodePackageId(nodeType));
            if (registered != null)
            {
                var httpClientFactory = ResolveOrFallback<IHttpClientFactory>(() => new TaskHttpClientFactory(), "IHttpClientFactory");
                var credentialAccessor = ResolveOrFallback<ICredentialAccessor>(() => new TaskCredentialAccessor(), "ICredentialAccessor");
                var loggerFactory = ResolveOrFallback<ILoggerFactory>(() => new LoggerFactory(), "ILoggerFactory");

                // In-process external-signal provider (supplied by a binary host plugin), if loaded —
                // lets reactive nodes dispatch/subscribe without an out-of-process hop.
                var externalSignals = _serviceProvider.GetService<IExternalSignalProvider>();

                return new BinaryPackageNodeTask(
                    registered.Executor,
                    httpClientFactory,
                    credentialAccessor,
                    loggerFactory.CreateLogger<BinaryPackageNodeTask>(),
                    externalSignals);
            }
        }
        catch (Exception ex)
        {
            // Unexpected: a registered binary-package registry threw while resolving. Fall through to the
            // database path but record it — this is not the benign "service absent" case (a null registry is
            // handled by the null-conditional above without throwing).
            Logger.LogWarning(ex, "Binary node-package resolution failed for node type '{NodeType}'.", nodeType);
        }

        // Try to resolve custom package from the database via the read seam.
        try
        {
            var packageStore = _serviceProvider.GetService<INodePackageReadStore>();
            if (packageStore != null)
            {
                var packageId = new NodePackageId(nodeType);
                var exists = packageStore.Exists(packageId);
                if (exists)
                {
                    var httpClientFactory = ResolveOrFallback<IHttpClientFactory>(() => new TaskHttpClientFactory(), "IHttpClientFactory");
                    var credentialAccessor = ResolveOrFallback<ICredentialAccessor>(() => new TaskCredentialAccessor(), "ICredentialAccessor");
                    var loggerFactory = ResolveOrFallback<ILoggerFactory>(() => new LoggerFactory(), "ILoggerFactory");

                    return new DynamicCustomNodeTask(
                        nodeType,
                        packageStore,
                        httpClientFactory,
                        credentialAccessor,
                        loggerFactory.CreateLogger<DynamicCustomNodeTask>(),
                        openApiSpecStore:   _serviceProvider.GetService<IOpenApiSpecStore>(),
                        serverConfigStore:  _serviceProvider.GetService<IServerConfigStore>(),
                        oAuthTokenCache:    _serviceProvider.GetService<IOAuthTokenCache>(),
                        interpreterFactory: _serviceProvider.GetService<IOpenApiInterpreterExecutorFactory>(),
                        capabilities:       _serviceProvider.GetService<ICapabilityPolicy>()
                    );
                }
            }
        }
        catch (Exception ex)
        {
            // Database/package-store access failed while probing for a custom node. Return null (node type
            // is treated as unknown) but record it so a real store failure isn't hidden.
            Logger.LogWarning(ex, "Database node-package resolution failed for node type '{NodeType}'.", nodeType);
        }

        return null;
    }

    // Dynamic Fallbacks for barebone test contexts where HTTP/Credentials DI is not configured
    private sealed class TaskHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    private sealed class TaskCredentialAccessor : ICredentialAccessor
    {
        public Task<string?> GetSecretAsync(string credentialRef, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }
}
