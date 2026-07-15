using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.Ai;

namespace Knotarium.Features.Nodes;

/// <summary>
/// Fallback <see cref="IAgentChatService"/>: throws a clear not-configured error at call time. Registered
/// as the default so a host that wires the built-in nodes without the AI slice still resolves
/// <see cref="AiAgentNodeTask"/> — the node then fails with this message. The AI slice's real service overrides it.
/// </summary>
public sealed class UnconfiguredAgentChatService : IAgentChatService
{
    public Task<AgentTurnResult> CompleteTurnAsync(AgentChatRequest request, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "The AI provider is not configured. Choose a provider, model, and API-key credential in Settings → AI Provider.");
}

/// <summary>
/// Fallback <see cref="IAgentToolRunner"/> for hosts that wire the built-in nodes without the Execution
/// slice (barebones test containers). Returns a failure result — never throws — so an <c>aiAgent</c> node
/// degrades to "every tool call fails" rather than the registry blowing up. The Execution slice's real
/// <c>AgentToolRunner</c> overrides it.
/// </summary>
public sealed class UnavailableAgentToolRunner : IAgentToolRunner
{
    public Task<AgentToolResult> RunToolAsync(AgentToolInvocation invocation, CancellationToken cancellationToken = default)
        => Task.FromResult(new AgentToolResult(
            false,
            JsonSerializer.Serialize(new { error = "the agent tool runner is not available in this host." }),
            Guid.Empty,
            "the agent tool runner is not available in this host."));
}
