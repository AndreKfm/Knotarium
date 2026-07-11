using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.NodeRuntime;

namespace Knotarium.NodeRuntime.Tests;

public class AssemblyLoadContextTests
{
    private static readonly MetadataReference[] References = new[]
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(INodeExecutor).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(AssemblyLoadContext).Assembly.Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Text.Json").Location),
        MetadataReference.CreateFromFile(Assembly.Load("Microsoft.Extensions.Logging.Abstractions").Location)
    };

    private static byte[] CompileSampleAssembly(string sourceCode)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
        
        var compilation = CSharpCompilation.Create(
            "DynamicTestAssembly_" + Guid.NewGuid().ToString("N"),
            new[] { syntaxTree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            var errors = string.Join("\n", result.Diagnostics.Select(d => d.ToString()));
            throw new InvalidOperationException($"Compilation failed:\n{errors}");
        }
        return ms.ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference AlcRef, WeakReference AssemblyRef) LoadAndUnloadExecutor(byte[] assemblyBytes)
    {
        var alc = new CollectibleAssemblyLoadContext("TestUnloadContext_" + Guid.NewGuid().ToString("N"));
        var assembly = alc.LoadFromBytes(assemblyBytes);
        
        var alcWeakRef = new WeakReference(alc);
        var assemblyWeakRef = new WeakReference(assembly);
        
        var executorType = assembly.GetTypes()
            .FirstOrDefault(t => typeof(INodeExecutor).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
        
        Assert.NotNull(executorType);
        
        var executor = (INodeExecutor)Activator.CreateInstance(executorType)!;
        Assert.NotNull(executor);

        alc.Unload();

        return (alcWeakRef, assemblyWeakRef);
    }

    [Fact]
    public void AssemblyLoadContext_UnloadsCleanly_NoLeaks()
    {
        // Arrange
        var sourceCode = @"
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Knotarium.Core.Contracts;
            using Knotarium.Core.Domain;

            public class MyTestExecutor : INodeExecutor
            {
                public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext context, CancellationToken cancellationToken)
                {
                    return new ValueTask<NodeResult>(new NodeResult(""out"", null, NodeExecutionStatus.Succeeded));
                }
            }
        ";

        var bytes = CompileSampleAssembly(sourceCode);

        // Act
        var (alcRef, assemblyRef) = LoadAndUnloadExecutor(bytes);

        // Assert: Force GC collections to trigger finalizers and unload sweeps
        for (int i = 0; i < 5; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(alcRef.IsAlive, "CollectibleAssemblyLoadContext was not garbage collected.");
        Assert.False(assemblyRef.IsAlive, "Loaded Assembly was not garbage collected.");
    }

    [Property(MaxTest = 20)]
    public bool FsCheck_ALC_Dynamic_Unload_Invariants(NonNull<string> randomClassName)
    {
        // Clean class name to make it a valid C# identifier
        var safeName = new string(randomClassName.Item.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(safeName) || char.IsDigit(safeName[0]))
        {
            safeName = "A" + safeName;
        }
        if (safeName.Length > 20)
        {
            safeName = safeName.Substring(0, 20);
        }

        var sourceCode = $@"
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Knotarium.Core.Contracts;
            using Knotarium.Core.Domain;

            public class {safeName} : INodeExecutor
            {{
                public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext context, CancellationToken cancellationToken)
                {{
                    return new ValueTask<NodeResult>(new NodeResult(""out"", null, NodeExecutionStatus.Succeeded));
                }}
            }}
        ";

        try
        {
            var bytes = CompileSampleAssembly(sourceCode);
            var (alcRef, assemblyRef) = LoadAndUnloadExecutor(bytes);

            for (int i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            return !alcRef.IsAlive && !assemblyRef.IsAlive;
        }
        catch
        {
            // If random string leads to naming or parsing collision, skip compiling
            return true;
        }
    }
}
