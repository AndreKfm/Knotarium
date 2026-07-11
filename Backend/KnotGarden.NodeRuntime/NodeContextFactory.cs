using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;

namespace KnotGarden.NodeRuntime;

public class IsolatedNodeContext : INodeContext
{
    public ILogger Logger { get; }
    public IWorkflowState State { get; }
    public IHttpClient? Http { get; }
    public ICredentialAccessor? Credentials { get; }

    public IsolatedNodeContext(
        ILogger logger,
        IWorkflowState state,
        IHttpClient? http,
        ICredentialAccessor? credentials)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        State = state ?? throw new ArgumentNullException(nameof(state));
        Http = http;
        Credentials = credentials;
    }
}

public static class NodeContextFactory
{
    public static INodeContext Create(
        ILogger logger,
        IWorkflowState state,
        IHttpClient? baseHttp,
        ICredentialAccessor? baseCredentials,
        IReadOnlyList<string> capabilities)
    {
        var hasHttp = capabilities.Any(c => string.Equals(c, "http", StringComparison.OrdinalIgnoreCase));
        var hasCredentials = capabilities.Any(c => string.Equals(c, "credentials", StringComparison.OrdinalIgnoreCase));

        return new IsolatedNodeContext(
            logger,
            state,
            hasHttp ? baseHttp : null,
            hasCredentials ? baseCredentials : null
        );
    }
}
