using KnotGarden.Features.Compiler;

// .NET convention: DI registration extensions live in Microsoft.Extensions.DependencyInjection
// so callers get AddCompiler() without an extra using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the workflow compiler and the built-in node-package manifest catalog. The host
/// still binds INodePackageManifestProvider to its DB-backed provider; this registers the
/// in-memory built-in catalog and the compiler entry point the rest of the system calls.
/// </summary>
public static class CompilerServiceCollectionExtensions
{
    public static IServiceCollection AddCompiler(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryNodePackageManifestProvider>();
        services.AddScoped<WorkflowCompiler>();
        return services;
    }
}
