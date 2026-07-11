using System;
using System.Linq;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Contracts.OpenApi;
using KnotGarden.Core.Domain;

namespace KnotGarden.Features.Nodes;

public class DependencyInjectionNodeTaskRegistry : INodeTaskRegistry
{
    private readonly IServiceProvider _serviceProvider;

    public DependencyInjectionNodeTaskRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
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
            var executorRegistry = _serviceProvider.GetService<KnotGarden.NodeRuntime.INodeExecutorRegistry>();
            var registered = executorRegistry?.GetLatest(new NodePackageId(nodeType));
            if (registered != null)
            {
                var httpClientFactory = _serviceProvider.GetService<IHttpClientFactory>() ?? new TaskHttpClientFactory();
                var credentialAccessor = _serviceProvider.GetService<ICredentialAccessor>() ?? new TaskCredentialAccessor();
                var loggerFactory = _serviceProvider.GetService<ILoggerFactory>() ?? new LoggerFactory();

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
        catch
        {
            // Registry unavailable (e.g. minimal test host) — fall through to the database path.
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
                    var httpClientFactory = _serviceProvider.GetService<IHttpClientFactory>() ?? new TaskHttpClientFactory();
                    var credentialAccessor = _serviceProvider.GetService<ICredentialAccessor>() ?? new TaskCredentialAccessor();
                    var loggerFactory = _serviceProvider.GetService<ILoggerFactory>() ?? new LoggerFactory();

                    return new DynamicCustomNodeTask(
                        nodeType,
                        packageStore,
                        httpClientFactory,
                        credentialAccessor,
                        loggerFactory.CreateLogger<DynamicCustomNodeTask>(),
                        openApiSpecStore:   _serviceProvider.GetService<IOpenApiSpecStore>(),
                        serverConfigStore:  _serviceProvider.GetService<IServerConfigStore>(),
                        oAuthTokenCache:    _serviceProvider.GetService<IOAuthTokenCache>(),
                        interpreterFactory: _serviceProvider.GetService<IOpenApiInterpreterExecutorFactory>()
                    );
                }
            }
        }
        catch
        {
            // Fallback for missing context or database access issues during mock test environments
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
