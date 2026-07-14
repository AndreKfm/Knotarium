using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Nodes;
using Xunit;

namespace Knotarium.Tests.Nodes;

/// <summary>
/// The compiled tier of a custom node package runs arbitrary in-process C#, so it is gated by the same
/// 'code execution' capability as the inline-code node. These tests pin the deny-by-default behaviour:
/// with the capability off — or no policy wired at all — the task must fail before compiling anything.
/// </summary>
public class DynamicCustomNodeTaskCapabilityTests
{
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    private sealed class StubCredentialAccessor : ICredentialAccessor
    {
        public Task<string?> GetSecretAsync(string credentialRef, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubCapabilityPolicy : ICapabilityPolicy
    {
        private readonly bool _enabled;
        public StubCapabilityPolicy(bool enabled) => _enabled = enabled;
        public Task<bool> IsEnabledAsync(string capability, CancellationToken cancellationToken = default) => Task.FromResult(_enabled);
    }

    private sealed class StubPackageReadStore : INodePackageReadStore
    {
        private readonly NodePackageVersion _version;
        public StubPackageReadStore(NodePackageVersion version) => _version = version;
        public bool Exists(NodePackageId id) => true;
        public Task<NodePackageVersion?> GetLatestVersionAsync(NodePackageId id, CancellationToken cancellationToken = default)
            => Task.FromResult<NodePackageVersion?>(_version);
    }

    private const string NodeType = "custom.compiled-thing";

    // A source that would be a hard compile error IF the gate ever let control reach the compiler —
    // proving the capability check short-circuits before compilation is attempted.
    private const string PoisonSource = "this is definitely not valid C#";

    private static DynamicCustomNodeTask CreateTask(ICapabilityPolicy? capabilities)
    {
        var version = new NodePackageVersion
        {
            Version = "1.0.0",
            ManifestJson = "{\"tier\":\"Compiled\"}",
            Source = PoisonSource,
            CreatedAt = DateTimeOffset.UtcNow
        };

        return new DynamicCustomNodeTask(
            NodeType,
            new StubPackageReadStore(version),
            new StubHttpClientFactory(),
            new StubCredentialAccessor(),
            NullLogger.Instance,
            capabilities: capabilities);
    }

    private static NodeExecutionContext Context() => new(
        WorkflowDefinitionId.New(),
        Guid.NewGuid(),
        new NodeId("custom1"),
        new Dictionary<string, object>(),
        new Dictionary<string, object>());

    [Fact]
    public async Task ExecuteAsync_CompiledTier_WhenCapabilityDisabled_ReturnsFailure_WithoutCompiling()
    {
        var task = CreateTask(new StubCapabilityPolicy(enabled: false));

        var result = await task.ExecuteAsync(Context(), CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("code execution", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        // The compiler was never invoked, so the (invalid) source never surfaced as a compile error.
        Assert.DoesNotContain("compilation", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_CompiledTier_WhenNoCapabilityPolicyWired_FailsClosed()
    {
        var task = CreateTask(capabilities: null);

        var result = await task.ExecuteAsync(Context(), CancellationToken.None);

        var failure = Assert.IsType<LegacyNodeResult.Failure>(result);
        Assert.Contains("code execution", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
