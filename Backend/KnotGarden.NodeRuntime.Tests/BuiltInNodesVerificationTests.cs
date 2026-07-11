using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.NodeRuntime;

namespace KnotGarden.NodeRuntime.Tests;

public class BuiltInNodesVerificationTests
{
    private static readonly string NodesPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "nodes"));

    private static readonly MetadataReference[] References = new[]
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(INodeExecutor).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(AssemblyLoadContext).Assembly.Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Text.Json").Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Net.Http").Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Memory").Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Private.Uri").Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Net.Primitives").Location),
        MetadataReference.CreateFromFile(Assembly.Load("Microsoft.Extensions.Logging.Abstractions").Location)
    };

    [Fact]
    public void VerifyAllNodesDirectoryStructure()
    {
        Assert.True(Directory.Exists(NodesPath), $"Nodes folder not found at: {NodesPath}");

        var subdirs = Directory.GetDirectories(NodesPath).Select(Path.GetFileName).ToList();
        var expectedDirs = new[]
        {
            "Start", "ManualTrigger", "WebhookTrigger", "Condition", "Switch",
            "SetVariable", "Transform", "Merge", "HttpRequest", "Delay", "Log", "End"
        };

        foreach (var dir in expectedDirs)
        {
            Assert.Contains(dir, subdirs);
            var dirPath = Path.Combine(NodesPath, dir);
            Assert.True(File.Exists(Path.Combine(dirPath, "manifest.json")), $"manifest.json missing in {dir}");
            Assert.True(File.Exists(Path.Combine(dirPath, "manifest.yaml")), $"manifest.yaml missing in {dir}");
            Assert.True(File.Exists(Path.Combine(dirPath, "icon.svg")), $"icon.svg missing in {dir}");
            Assert.True(File.Exists(Path.Combine(dirPath, "tests", "cases.yaml")), $"tests/cases.yaml missing in {dir}");
        }
    }

    [Fact]
    public void VerifyManifestsAreDeserializable()
    {
        var expectedDirs = new[]
        {
            "Start", "ManualTrigger", "WebhookTrigger", "Condition", "Switch",
            "SetVariable", "Transform", "Merge", "HttpRequest", "Delay", "Log", "End"
        };

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

        foreach (var dir in expectedDirs)
        {
            var manifestPath = Path.Combine(NodesPath, dir, "manifest.json");
            var json = File.ReadAllText(manifestPath);

            var manifest = JsonSerializer.Deserialize<NodePackageManifest>(json, options);
            Assert.NotNull(manifest);
            Assert.Equal(dir.ToLowerInvariant(), manifest.Id.Value.ToLowerInvariant());
            Assert.False(string.IsNullOrEmpty(manifest.Version));
            Assert.False(string.IsNullOrEmpty(manifest.DisplayName));
            Assert.False(string.IsNullOrEmpty(manifest.Category));
        }
    }

    [Fact]
    public void VerifyTier2ExecutorsCompileAndPassBannedApiAnalysis()
    {
        var tier2Dirs = new[] { "WebhookTrigger", "Merge", "HttpRequest", "Delay" };

        foreach (var dir in tier2Dirs)
        {
            var dirPath = Path.Combine(NodesPath, dir);
            var executorPath = Path.Combine(dirPath, "Executor.cs");
            Assert.True(File.Exists(executorPath), $"Executor.cs missing in compiled node: {dir}");

            var sourceCode = File.ReadAllText(executorPath);

            // 1. Roslyn static analysis (Banned API verification)
            var analysisResult = BannedApiAnalyzer.Analyze(sourceCode, dir);
            var errors = analysisResult.Where(r => r.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Empty(errors);

            // 2. Roslyn compilation verification
            var bytes = CompileAssembly(sourceCode, dir);
            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 0);

            // 3. Load test context and verify INodeExecutor implementation
            var alc = new CollectibleAssemblyLoadContext($"Verification_{dir}");
            var assembly = alc.LoadFromBytes(bytes);
            var executorType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(INodeExecutor).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

            Assert.NotNull(executorType);
            var executor = Activator.CreateInstance(executorType) as INodeExecutor;
            Assert.NotNull(executor);

            alc.Unload();
        }
    }

    private static byte[] CompileAssembly(string sourceCode, string assemblyName)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
        
        var compilation = CSharpCompilation.Create(
            $"DynamicVerification_{assemblyName}_{Guid.NewGuid():N}",
            new[] { syntaxTree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            var errors = string.Join("\n", result.Diagnostics.Select(d => d.ToString()));
            throw new InvalidOperationException($"Verification compilation of {assemblyName} failed:\n{errors}");
        }
        return ms.ToArray();
    }
}
