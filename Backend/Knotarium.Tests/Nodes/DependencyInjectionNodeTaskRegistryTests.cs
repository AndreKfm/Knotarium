using Microsoft.Extensions.DependencyInjection;
using Knotarium.Core.Contracts;
using Knotarium.Features.Execution;
using Knotarium.Features.Nodes;
using Xunit;

namespace Knotarium.Tests.Nodes;

public class DependencyInjectionNodeTaskRegistryTests
{
    [Fact]
    public void GetTask_ResolvesHttpRequestTaskWithinScope()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ExecutionTelemetry>();
        services.AddScoped<ISecretResolver, FakeSecretResolver>();
        services.AddHttpClient("HttpNode");
        services.AddTransient<HttpRequestNodeTask>();
        services.AddScoped<INodeTaskRegistry, DependencyInjectionNodeTaskRegistry>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<INodeTaskRegistry>();
        var task = registry.GetTask("httpRequest");

        Assert.NotNull(task);
        Assert.IsType<HttpRequestNodeTask>(task);
    }

    [Fact]
    public void GetTask_ResolvesAiPromptTask_ViaTheBuiltInRegistration()
    {
        // AddBuiltInNodes alone must be enough: its TryAdd chat-completion fallback satisfies the
        // AiPromptNodeTask dependency so a barebone host resolves the task (and fails at run time
        // with a clear not-configured message instead of a DI error).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBuiltInNodes();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<INodeTaskRegistry>();
        var task = registry.GetTask("aiPrompt");

        Assert.NotNull(task);
        Assert.IsType<AiPromptNodeTask>(task);
    }

    private sealed class FakeSecretResolver : ISecretResolver
    {
        public Task<string?> ResolveAsync(string secretRef, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }
    }
}