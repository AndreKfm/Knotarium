// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.NodeRuntime;

namespace Knotarium.Features.NodeEditor;

/// <summary>
/// Compiles a node-editor executor draft in memory. Accepts either a full
/// <see cref="INodeExecutor"/> implementation or a bare script body, which is wrapped
/// into a generated executor class before compilation.
/// </summary>
internal static class SandboxExecutorCompiler
{
    public static bool TryCompileExecutor(string sourceCode, out byte[] assemblyBytes, out List<string> errors)
    {
        bool isFullExecutor = sourceCode.Contains("class ") && sourceCode.Contains("INodeExecutor");
        string codeToCompile = isFullExecutor ? sourceCode : WrapScriptCode(sourceCode);

        var syntaxTree = CSharpSyntaxTree.ParseText(codeToCompile);
        var references = BuildReferences();

        var compilation = CSharpCompilation.Create(
            $"NodeEditorDraft_{Guid.NewGuid():N}",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            assemblyBytes = Array.Empty<byte>();
            errors = result.Diagnostics
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();
            return false;
        }

        assemblyBytes = ms.ToArray();
        errors = new List<string>();
        return true;
    }

    private static string WrapScriptCode(string scriptCode)
    {
        return $@"using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;

namespace Knotarium.Nodes;

public class ScriptInputHelper
{{
    private readonly NodeInput _input;
    public ScriptInputHelper(NodeInput input) => _input = input;

    public T? Get<T>(string name, T? defaultValue = default)
    {{
        if (!_input.Parameters.TryGetValue(name, out var element)) return defaultValue;
        try
        {{
            var options = new JsonSerializerOptions {{ PropertyNameCaseInsensitive = true }};
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            return JsonSerializer.Deserialize<T>(element.GetRawText(), options);
        }}
        catch {{ return defaultValue; }}
    }}
}}

public class DynamicScriptExecutor : INodeExecutor
{{
    public async ValueTask<NodeResult> ExecuteAsync(
        NodeInput input,
        INodeContext context,
        CancellationToken cancellationToken)
    {{
        var Input = new ScriptInputHelper(input);
        var Logger = context.Logger;

        NodeResult Success(object? payload = null) =>
            new NodeResult(""success"", payload != null ? JsonSerializer.SerializeToElement(payload) : null, NodeExecutionStatus.Succeeded);

        NodeResult Fail(string error) =>
            new NodeResult(""error"", JsonSerializer.SerializeToElement(new {{ error }}), NodeExecutionStatus.Failed);

        try
        {{
            #line 1 ""UserScript""
{scriptCode}
        }}
        catch (Exception ex)
        {{
            return Fail(ex.Message);
        }}
    }}
}}
";
    }

    private static IEnumerable<MetadataReference> BuildReferences()
    {
        var assemblies = new[]
        {
            typeof(object).Assembly,
            typeof(INodeExecutor).Assembly,
            typeof(CollectibleAssemblyLoadContext).Assembly,
            typeof(HttpRequestMessage).Assembly,
            typeof(ILogger).Assembly,
            Assembly.Load("System.Runtime"),
            Assembly.Load("System.Collections"),
            Assembly.Load("System.Threading.Tasks"),
            Assembly.Load("System.Text.Json"),
            Assembly.Load("System.Private.Uri")
        };

        return assemblies
            .Select(a => a.Location)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
