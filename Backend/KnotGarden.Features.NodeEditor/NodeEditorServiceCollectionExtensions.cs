using KnotGarden.Features.NodeEditor;

// .NET convention: DI registration extensions live in Microsoft.Extensions.DependencyInjection
// so callers get AddNodeEditor() without an extra using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the in-app node-authoring sandbox: the session gate that tracks which
/// package+version has a passing sandbox run (publish gate), and the Roslyn-backed
/// sandbox service that compiles and runs a draft node against recording capabilities.
/// </summary>
public static class NodeEditorServiceCollectionExtensions
{
    public static IServiceCollection AddNodeEditor(this IServiceCollection services)
    {
        services.AddSingleton<INodeEditorSessionGate, NodeEditorSessionGate>();
        services.AddScoped<INodeEditorSandboxService, NodeEditorSandboxService>();
        return services;
    }
}
