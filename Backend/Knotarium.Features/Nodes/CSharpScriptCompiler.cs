// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

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
using Knotarium.Core.Contracts;
using Knotarium.Core.Contracts.OpenApi;
using Knotarium.Core.Domain;
using Knotarium.NodeRuntime;

namespace Knotarium.Features.Nodes;

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
/// compiles only once per process. Source is screened by <see cref="BannedApiAnalyzer"/>
/// before compilation (see <see cref="EnforceBannedApiAnalysis"/>) — a best-effort linter,
/// <b>not</b> a sandbox. Execution itself is <b>not sandboxed</b>: scripts run with full backend
/// process access (trusted-author posture) and are bounded only by the caller's CancellationToken.
/// Real isolation (separate process + OS resource limits) is tracked separately.
/// </summary>
public sealed class CSharpScriptCompiler
{
    // Compiled types are cached so identical source compiles only once per process. Each entry keeps its
    // collectible load context so eviction can Unload() it. Bounded to avoid unbounded growth on a
    // long-running server that sees many distinct inline scripts / package versions.
    private sealed class CompiledEntry
    {
        public required Type Type { get; init; }
        public required CollectibleAssemblyLoadContext LoadContext { get; init; }
        // Retained so the out-of-process sandbox can ship the exact emitted assembly to a worker
        // without recompiling. Bounded by MaxCachedTypes; script assemblies are small (tens of KB).
        public required byte[] AssemblyBytes { get; init; }
        public long Sequence { get; init; }
    }

    // Ordinal, not OrdinalIgnoreCase: cache keys are opaque (a SHA-256 hash for inline code, a
    // "type_version_ticks" string for compiled packages). Case-folding them risks collapsing two keys
    // that should map to distinct compiled types.
    private static readonly ConcurrentDictionary<string, Lazy<CompiledEntry>> _compiledCache
        = new(StringComparer.Ordinal);
    private static long _sequenceCounter;
    private static readonly object _evictionLock = new();

    // Max distinct compiled types held at once. Internal + mutable only so tests can shrink it; the
    // default holds in production. Eviction Unload()s the oldest entry's load context — safe even while a
    // run still uses the type, because Unload() only *requests* collection once no strong refs remain.
    internal static int MaxCachedTypes = 256;

    // When true, user source is screened by BannedApiAnalyzer *before* it is compiled and run — the same
    // gate the node editor applies at authoring time, now on the execution path. Secure-by-default;
    // operators can disable via Security:Sandbox:AnalyzeAtRuntime for legacy packages. Static (not an
    // instance field) because DynamicCustomNodeTask/BinaryPackageNodeTask each `new` their own compiler,
    // mirroring MaxCachedTypes. This screening is a linter, NOT a sandbox — see the type-level remarks.
    public static bool EnforceBannedApiAnalysis = true;
    internal static int CachedTypeCount => _compiledCache.Count;
    internal static bool ContainsCompiledKey(string key)
        => _compiledCache.TryGetValue(key, out var lazy) && lazy.IsValueCreated;

    /// <summary>True when the source is already a full INodeExecutor class (vs. a bare script body).</summary>
    public static bool IsFullExecutor(string source)
        => source.Contains("class ") && source.Contains("INodeExecutor");

    /// <summary>
    /// Compile <paramref name="source"/> (a full INodeExecutor class or a bare script body) into an
    /// executor <see cref="Type"/>, caching by <paramref name="cacheKey"/>. Throws
    /// <see cref="ScriptCompilationException"/> with formatted diagnostics on failure.
    /// </summary>
    public Type GetOrCompile(string cacheKey, string source)
        => GetOrCompileEntry(cacheKey, source).Type;

    /// <summary>
    /// Like <see cref="GetOrCompile"/> but also returns the emitted assembly bytes, which the
    /// out-of-process sandbox ships to a worker instead of instantiating the type host-side.
    /// </summary>
    public (Type Type, byte[] AssemblyBytes) GetOrCompileWithBytes(string cacheKey, string source)
    {
        var entry = GetOrCompileEntry(cacheKey, source);
        return (entry.Type, entry.AssemblyBytes);
    }

    private CompiledEntry GetOrCompileEntry(string cacheKey, string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ScriptCompilationException("Script source code is missing.");

        // Lazy makes the same key compile exactly once even under concurrent first-use: other callers
        // block on the same Lazy instead of racing to build (and orphan) a second load context.
        var lazy = _compiledCache.GetOrAdd(cacheKey,
            _ => new Lazy<CompiledEntry>(() => Compile(cacheKey, source), LazyThreadSafetyMode.ExecutionAndPublication));

        CompiledEntry entry;
        try
        {
            entry = lazy.Value;
        }
        catch
        {
            // A failed compile must not poison the cache — drop this exact lazy so a later call retries.
            _compiledCache.TryRemove(new KeyValuePair<string, Lazy<CompiledEntry>>(cacheKey, lazy));
            throw;
        }

        EvictIfNeeded();
        return entry;
    }

    private static CompiledEntry Compile(string cacheKey, string source)
    {
        string codeToCompile = IsFullExecutor(source) ? source : WrapScriptCode(source);

        // Screen the source before compiling/running it. This is the same banned-API / static-mutable-state
        // gate the node editor runs at authoring time — historically it only guarded the editor "Test"
        // button, never execution. Deterministic per source, and Compile is cached, so this runs once per
        // distinct script. It is a best-effort linter, not a security boundary (see the class remarks).
        if (EnforceBannedApiAnalysis)
        {
            var findings = BannedApiAnalyzer.Analyze(codeToCompile)
                .Where(d => d.Severity == AnalysisSeverity.Error)
                .ToList();
            if (findings.Count > 0)
            {
                var detail = string.Join("\n", findings.Select(d => $"  [{d.Code}] {d.Message} (line {d.StartLine})"));
                throw new ScriptCompilationException($"Rejected by security analysis:\n{detail}");
            }
        }

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
        try
        {
            var assembly = loadContext.LoadFromBytes(assemblyBytes);
            var executorType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(INodeExecutor).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

            if (executorType == null)
                throw new ScriptCompilationException("No class implementing INodeExecutor found in the compiled assembly.");

            return new CompiledEntry
            {
                Type = executorType,
                LoadContext = loadContext,
                AssemblyBytes = assemblyBytes,
                Sequence = Interlocked.Increment(ref _sequenceCounter),
            };
        }
        catch
        {
            // Any failure after LoadFromBytes (a ReflectionTypeLoadException from GetTypes, or no executor
            // found) must unload the context so a failed compile never leaks a collectible ALC.
            loadContext.Unload();
            throw;
        }
    }

    /// <summary>
    /// Keep the compiled-type cache bounded. Evicts the oldest entries (by compile sequence) and unloads
    /// their load contexts. Runs off the hot path (only after a fresh compile), under a lock so concurrent
    /// compiles don't over-evict or double-unload.
    /// </summary>
    private static void EvictIfNeeded()
    {
        if (_compiledCache.Count <= MaxCachedTypes)
            return;

        lock (_evictionLock)
        {
            while (_compiledCache.Count > MaxCachedTypes)
            {
                string? oldestKey = null;
                var oldestSeq = long.MaxValue;
                foreach (var kvp in _compiledCache)
                {
                    if (!kvp.Value.IsValueCreated)
                        continue; // still compiling on another thread — leave it alone
                    var seq = kvp.Value.Value.Sequence;
                    if (seq < oldestSeq)
                    {
                        oldestSeq = seq;
                        oldestKey = kvp.Key;
                    }
                }

                if (oldestKey == null)
                    break;

                if (_compiledCache.TryRemove(oldestKey, out var removed) && removed.IsValueCreated)
                    removed.Value.LoadContext.Unload();
            }
        }
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
        var nodeInput = new NodeInput(BuildInputs(context, extraInputs));

        var recordingState = new TaskWorkflowState(context);
        var recordingHttpClient = new TaskHttpClient(httpClientFactory);
        var recordingContext = new TaskNodeContext(logger, recordingState, recordingHttpClient, credentialAccessor, externalSignals);

        NodeResult result;
        try
        {
            result = await executor.ExecuteAsync(nodeInput, recordingContext, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A full-executor (non-wrapped) source can throw straight out of ExecuteAsync; the wrapped-
            // script path already normalizes internally, so mirror it here to keep both tiers consistent.
            return new LegacyNodeResult.Failure("Execution cancelled.");
        }
        catch (Exception ex)
        {
            return new LegacyNodeResult.Failure(ex.Message);
        }

        return NormalizeNodeResult(result);
    }

    /// <summary>Merges context inputs with extras into the JSON parameter map a <see cref="NodeInput"/> carries.</summary>
    internal static Dictionary<string, JsonElement> BuildInputs(
        NodeExecutionContext context, IReadOnlyDictionary<string, JsonElement>? extraInputs)
    {
        var inputs = context.Inputs.ToDictionary(kvp => kvp.Key, kvp => JsonSerializer.SerializeToElement(kvp.Value));
        if (extraInputs != null)
        {
            foreach (var kvp in extraInputs)
                inputs[kvp.Key] = kvp.Value;
        }
        return inputs;
    }

    /// <summary>
    /// Normalizes an executor's <see cref="NodeResult"/> into a <see cref="LegacyNodeResult"/>.
    /// Shared by the in-process path above and the out-of-process sandbox runner, so a workflow
    /// sees identical results regardless of where the executor ran.
    /// </summary>
    internal static LegacyNodeResult NormalizeNodeResult(NodeResult result)
    {
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
        "Knotarium.Core.Contracts", "Knotarium.Core.Domain",
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
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
{extraUsings}

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
            typeof(INodeExecutor).Assembly,        // Knotarium.Core (also has IOpenApiSpecStore etc.)
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

    internal sealed class TaskWorkflowState : IWorkflowState
    {
        // Reused across every GetVariable call — JsonSerializerOptions is immutable once used and
        // expensive to allocate, so a single shared instance avoids per-read allocation.
        private static readonly JsonSerializerOptions VariableJsonOptions = CreateVariableJsonOptions();
        private static JsonSerializerOptions CreateVariableJsonOptions()
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            return options;
        }

        private readonly NodeExecutionContext _context;
        // When this node was inlined from a subflow, its id is prefixed (e.g. "subflow-a/inline-1").
        // Variable names accessed from Inline Code by string literal aren't rewritten by the compiler,
        // so scope them here to the subflow instance — keeping them isolated like every other access.
        private readonly string _scope;
        public TaskWorkflowState(NodeExecutionContext context)
        {
            _context = context;
            _scope = Knotarium.Features.Compiler.SubflowScope.ForNodeId(context.NodeId.Value);
        }
        public T? GetVariable<T>(string name)
        {
            var scopedName = Knotarium.Features.Compiler.SubflowScope.Apply(_scope, name);
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
                    return JsonSerializer.Deserialize<T>(element.GetRawText(), VariableJsonOptions);
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
            var scopedName = Knotarium.Features.Compiler.SubflowScope.Apply(_scope, name);
            if (value == null) _context.GlobalVariables.Remove(scopedName);
            else _context.GlobalVariables[scopedName] = value;
        }
        public JsonElement? GetNodeOutput(NodeId nodeId, string outputName)
            // Previously returned null silently, which read as "this node produced no output" — a footgun.
            // Inline/compiled scripts should reference upstream outputs via inputs or workflow variables.
            => throw new NotSupportedException(
                "GetNodeOutput is not available inside inline/compiled script nodes. " +
                "Reference upstream node outputs through the node's inputs or workflow variables instead.");
    }

    internal sealed class TaskHttpClient : IHttpClient
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
