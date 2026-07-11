using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Contracts.OpenApi;
using KnotGarden.Core.Domain;
using KnotGarden.NodeRuntime;

namespace KnotGarden.Features.Nodes;

/// <summary>
/// Thrown when user-supplied C# source fails to compile (or produces no usable executor).
/// The message carries the formatted Roslyn diagnostics so callers can surface them directly.
/// </summary>
public sealed class ScriptCompilationException : Exception
{
    public ScriptCompilationException(string message) : base(message) { }
}

/// <summary>
/// Shared Roslyn compile + run pipeline for user-authored C#. Used both by
/// <see cref="DynamicCustomNodeTask"/> (DB-stored custom packages) and by
/// <see cref="InlineCodeNodeTask"/> (scripts typed directly into a workflow node).
///
/// Compiled types are cached by an opaque caller-supplied key, so identical source
/// compiles only once per process. Execution is <b>not sandboxed</b>: scripts run with
/// full backend process access (trusted-author posture) and are bounded only by the
/// caller's CancellationToken.
/// </summary>
public sealed class CSharpScriptCompiler
{
    // Cache compiled types to avoid re-compiling the same source on every run session.
    private static readonly ConcurrentDictionary<string, (Type Type, CollectibleAssemblyLoadContext LoadContext)> _compiledCache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the source is already a full INodeExecutor class (vs. a bare script body).</summary>
    public static bool IsFullExecutor(string source)
        => source.Contains("class ") && source.Contains("INodeExecutor");

    /// <summary>
    /// Compile <paramref name="source"/> (a full INodeExecutor class or a bare script body) into an
    /// executor <see cref="Type"/>, caching by <paramref name="cacheKey"/>. Throws
    /// <see cref="ScriptCompilationException"/> with formatted diagnostics on failure.
    /// </summary>
    public Type GetOrCompile(string cacheKey, string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ScriptCompilationException("Script source code is missing.");

        if (_compiledCache.TryGetValue(cacheKey, out var cached))
            return cached.Type;

        string codeToCompile = IsFullExecutor(source) ? source : WrapScriptCode(source);

        var syntaxTree = CSharpSyntaxTree.ParseText(codeToCompile);
        var references = BuildReferences();

        var compilation = CSharpCompilation.Create(
            $"NodeRuntime_{Guid.NewGuid():N}",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        using var ms = new MemoryStream();
        var compileResult = compilation.Emit(ms);

        if (!compileResult.Success)
        {
            var errors = string.Join("\n", compileResult.Diagnostics
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            throw new ScriptCompilationException($"C# compilation failed:\n{errors}");
        }

        var assemblyBytes = ms.ToArray();
        var loadContext = new CollectibleAssemblyLoadContext($"DynamicRun_{cacheKey}");
        var assembly = loadContext.LoadFromBytes(assemblyBytes);
        var executorType = assembly.GetTypes()
            .FirstOrDefault(t => typeof(INodeExecutor).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        if (executorType == null)
        {
            loadContext.Unload();
            throw new ScriptCompilationException("No class implementing INodeExecutor found in the compiled assembly.");
        }

        var entry = (executorType, loadContext);
        _compiledCache[cacheKey] = entry;
        return executorType;
    }

    /// <summary>
    /// Instantiate a compiled executor type, satisfying a constructor from <paramref name="knownServices"/>
    /// when possible, otherwise falling back to the parameterless constructor.
    /// </summary>
    public INodeExecutor Instantiate(Type executorType, IReadOnlyDictionary<Type, object?>? knownServices = null)
    {
        if (knownServices != null && knownServices.Count > 0)
        {
            var matchedCtor = executorType.GetConstructors()
                .FirstOrDefault(c => c.GetParameters().All(p => knownServices.ContainsKey(p.ParameterType)));
            if (matchedCtor != null)
            {
                return (INodeExecutor)matchedCtor.Invoke(
                    matchedCtor.GetParameters().Select(p => knownServices[p.ParameterType]).ToArray());
            }
        }
        return (INodeExecutor)Activator.CreateInstance(executorType)!;
    }

    /// <summary>
    /// Run an executor against a workflow execution context and normalize its <see cref="NodeResult"/>
    /// into a <see cref="LegacyNodeResult"/>. <paramref name="extraInputs"/> are merged on top of the
    /// context inputs (used e.g. to inject the reserved OpenAPI spec id).
    /// </summary>
    public async Task<LegacyNodeResult> RunAsync(
        INodeExecutor executor,
        NodeExecutionContext context,
        IHttpClientFactory httpClientFactory,
        ICredentialAccessor credentialAccessor,
        ILogger logger,
        IReadOnlyDictionary<string, JsonElement>? extraInputs = null,
        CancellationToken cancellationToken = default,
        IExternalSignalProvider? externalSignals = null)
    {
        var inputs = context.Inputs.ToDictionary(kvp => kvp.Key, kvp => JsonSerializer.SerializeToElement(kvp.Value));
        if (extraInputs != null)
        {
            foreach (var kvp in extraInputs)
                inputs[kvp.Key] = kvp.Value;
        }
        var nodeInput = new NodeInput(inputs);

        var recordingState = new TaskWorkflowState(context);
        var recordingHttpClient = new TaskHttpClient(httpClientFactory);
        var recordingContext = new TaskNodeContext(logger, recordingState, recordingHttpClient, credentialAccessor, externalSignals);

        var result = await executor.ExecuteAsync(nodeInput, recordingContext, cancellationToken);

        var dictPayload = new Dictionary<string, object>();
        if (result.Payload != null && result.Payload.Value.ValueKind != JsonValueKind.Null)
        {
            if (result.Payload.Value.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    dictPayload = JsonSerializer.Deserialize<Dictionary<string, object>>(result.Payload.Value.GetRawText()) ?? new();
                }
                catch
                {
                    dictPayload = new Dictionary<string, object> { ["value"] = result.Payload.Value };
                }
            }
            else
            {
                dictPayload = new Dictionary<string, object> { ["value"] = result.Payload.Value };
            }
        }

        if (result.Status == NodeExecutionStatus.Succeeded)
        {
            dictPayload["selectedPort"] = result.OutputName;
            return new LegacyNodeResult.Success(dictPayload);
        }
        else if (result.Status == NodeExecutionStatus.Cancelled)
        {
            return new LegacyNodeResult.Failure("Execution cancelled.");
        }
        else
        {
            var errorMsg = dictPayload.TryGetValue("error", out var err) ? err?.ToString() : "Node execution failed.";
            return new LegacyNodeResult.Failure(errorMsg ?? "Node execution failed.");
        }
    }

    // using directives the wrapper already provides — user duplicates are dropped to avoid noise.
    private static readonly HashSet<string> _defaultUsings = new(StringComparer.Ordinal)
    {
        "System", "System.Collections.Generic", "System.Linq", "System.Text.Json",
        "System.Threading", "System.Threading.Tasks", "Microsoft.Extensions.Logging",
        "KnotGarden.Core.Contracts", "KnotGarden.Core.Domain",
    };

    // Matches a leading using *directive* (e.g. "using System;", "using static System.Math;",
    // "using Foo = Bar;") but NOT a "using (resource)" statement.
    private static readonly System.Text.RegularExpressions.Regex _usingDirective =
        new(@"^\s*using\s+(static\s+)?[^;()]+;\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Lets users paste "full program" style scripts: hoist any leading using-directives out of the
    /// method body up to namespace scope, and return the remaining statement body.
    /// </summary>
    private static (string Usings, string Body) HoistUsings(string scriptCode)
    {
        var lines = scriptCode.Replace("\r\n", "\n").Split('\n');
        var hoisted = new List<string>();
        int i = 0;
        // Only consume directives from the contiguous leading block (blank/comment lines allowed
        // between them); stop at the first real statement so mid-body "using (...)" is untouched.
        for (; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//"))
                continue;
            if (_usingDirective.IsMatch(lines[i]))
            {
                var ns = trimmed["using ".Length..].TrimEnd(';').Trim();
                if (!ns.StartsWith("static ") && !ns.Contains('=') && _defaultUsings.Contains(ns))
                {
                    // already provided by the wrapper — drop the duplicate
                }
                else
                {
                    hoisted.Add(trimmed.EndsWith(";") ? trimmed : trimmed + ";");
                }
                continue;
            }
            break;
        }

        var body = string.Join("\n", lines[i..]);
        return (string.Join("\n", hoisted), body);
    }

    private static string WrapScriptCode(string rawScript)
    {
        var (extraUsings, scriptCode) = HoistUsings(rawScript);
        return $@"using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
{extraUsings}

namespace KnotGarden.Nodes;

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
#pragma warning disable CS0162 // a trailing user 'return' makes the default below unreachable — that's fine
            #line 1 ""UserScript""
{scriptCode}
            #line default
            // Default result when the script runs purely for side effects and never returns.
            return Success();
#pragma warning restore CS0162
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
            typeof(INodeExecutor).Assembly,        // KnotGarden.Core (also has IOpenApiSpecStore etc.)
            typeof(IOAuthTokenCache).Assembly,     // same as above — explicit for clarity
            typeof(CollectibleAssemblyLoadContext).Assembly,
            typeof(HttpRequestMessage).Assembly,
            typeof(ILogger).Assembly,
            typeof(Enumerable).Assembly,           // System.Linq
            typeof(Console).Assembly,              // System.Console
            Assembly.Load("System.Runtime"),
            Assembly.Load("System.Collections"),
            Assembly.Load("System.Linq"),
            Assembly.Load("System.Console"),
            Assembly.Load("System.Threading.Tasks"),
            Assembly.Load("System.Text.Json"),
            Assembly.Load("System.Private.Uri"),
            Assembly.Load("System.Net.Primitives"),
            Assembly.Load("System.Memory"),
            Assembly.Load("netstandard"),
        };

        return assemblies
            .Select(a => a.Location)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    // Context Mapping Helpers
    private sealed class TaskNodeContext : INodeContext
    {
        public ILogger Logger { get; }
        public IWorkflowState State { get; }
        public IHttpClient? Http { get; }
        public ICredentialAccessor? Credentials { get; }
        public IExternalSignalProvider? ExternalSignals { get; }

        public TaskNodeContext(ILogger logger, IWorkflowState state, IHttpClient? http, ICredentialAccessor? credentials, IExternalSignalProvider? externalSignals = null)
        {
            Logger = logger;
            State = state;
            Http = http;
            Credentials = credentials;
            ExternalSignals = externalSignals;
        }
    }

    private sealed class TaskWorkflowState : IWorkflowState
    {
        private readonly NodeExecutionContext _context;
        // When this node was inlined from a subflow, its id is prefixed (e.g. "subflow-a/inline-1").
        // Variable names accessed from Inline Code by string literal aren't rewritten by the compiler,
        // so scope them here to the subflow instance — keeping them isolated like every other access.
        private readonly string _scope;
        public TaskWorkflowState(NodeExecutionContext context)
        {
            _context = context;
            _scope = KnotGarden.Features.Compiler.SubflowScope.ForNodeId(context.NodeId.Value);
        }
        public T? GetVariable<T>(string name)
        {
            var scopedName = KnotGarden.Features.Compiler.SubflowScope.Apply(_scope, name);
            if (!_context.GlobalVariables.TryGetValue(scopedName, out var val) || val is null)
                return default;

            // Convert across the boxed representations a variable can take (a Set Variable node
            // stores "0" as a string/JsonElement, code stores a boxed int, etc.) — mirrors
            // VariableBag.Get so GetVariable<int>("count") works regardless of how it was set.
            if (val is T typed) return typed;

            if (val is JsonElement element)
            {
                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                    return JsonSerializer.Deserialize<T>(element.GetRawText(), options);
                }
                catch { return default; }
            }

            try
            {
                return (T)Convert.ChangeType(val, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return default; }
        }
        public void SetVariable(string name, object? value)
        {
            var scopedName = KnotGarden.Features.Compiler.SubflowScope.Apply(_scope, name);
            if (value == null) _context.GlobalVariables.Remove(scopedName);
            else _context.GlobalVariables[scopedName] = value;
        }
        public JsonElement? GetNodeOutput(NodeId nodeId, string outputName) => null;
    }

    private sealed class TaskHttpClient : IHttpClient
    {
        private readonly IHttpClientFactory _factory;
        public TaskHttpClient(IHttpClientFactory factory) => _factory = factory;
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var client = _factory.CreateClient("HttpNode");
            return await client.SendAsync(request, cancellationToken);
        }
    }
}
