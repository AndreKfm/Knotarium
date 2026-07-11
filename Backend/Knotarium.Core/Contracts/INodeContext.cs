using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Domain;

namespace Knotarium.Core.Contracts;

public interface IWorkflowState
{
    T? GetVariable<T>(string name);
    void SetVariable(string name, object? value);
    JsonElement? GetNodeOutput(NodeId nodeId, string outputName);

    /// <summary>
    /// Resolve a variable / promoted node-output, reporting <b>found-ness</b> so callers can tell a
    /// missing reference apart from one that resolved to a legitimate <c>null</c>. Returns the raw
    /// stored value (unconverted). The Condition engine relies on this distinction: a missing ref is
    /// <c>RESOLUTION_FAILED</c> (fail-node), a resolved <c>null</c> is a legitimate value.
    /// The default is a best-effort fallback for stub implementations and CANNOT distinguish
    /// missing from resolved-null; the real executor state projections override it precisely.
    /// </summary>
    bool TryResolveVariable(string name, out object? value)
    {
        value = GetVariable<object>(name);
        return value is not null;
    }
}

public interface IHttpClient
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

public interface ICredentialAccessor
{
    Task<string?> GetSecretAsync(string credentialRef, CancellationToken cancellationToken = default);
}

public interface INodeContext
{
    ILogger Logger { get; }
    IWorkflowState State { get; }
    IHttpClient? Http { get; }
    ICredentialAccessor? Credentials { get; }

    /// <summary>
    /// In-process gateway to the external reactive-signalling provider supplied by a binary
    /// <see cref="IHostPlugin"/>, or <c>null</c> when no provider is loaded or the node did not
    /// declare the <c>externalSignals</c> capability. Lets graph nodes dispatch/subscribe without
    /// an out-of-process hop. Default-null so existing context implementations are unaffected.
    /// </summary>
    IExternalSignalProvider? ExternalSignals => null;
}
