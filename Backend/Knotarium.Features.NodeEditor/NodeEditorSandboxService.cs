using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.NodeRuntime;

namespace Knotarium.Features.NodeEditor;

public sealed class NodeEditorSandboxService : INodeEditorSandboxService
{
    private sealed partial class ManifestDocument
    {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = "1.0.0";
        public string DisplayName { get; set; } = string.Empty;
        public string Category { get; set; } = "Utility";
        public string Tier { get; set; } = nameof(NodeTier.Compiled);
        public string SideEffectKindName { get; set; } = nameof(NodeSideEffectKind.IdempotentSideEffect);
        public string RecoveryModeName { get; set; } = nameof(RecoveryMode.FailImmediately);
        public int DefaultTimeoutSeconds { get; set; }
        public List<string> Capabilities { get; set; } = new();
        public List<ParameterDocument> Parameters { get; set; } = new();
        public List<OutputDocument> Outputs { get; set; } = new();
    }

    private sealed class ParameterDocument
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = "string";
        public bool Required { get; set; }
        public bool Expression { get; set; }
        public List<string>? Values { get; set; }
    }

    private sealed class OutputDocument
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class TestsDocument
    {
        public List<TestCaseDocument> Cases { get; set; } = new();
    }

    private sealed class TestCaseDocument
    {
        public string Name { get; set; } = "Unnamed test";
        public Dictionary<string, object?> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string ExpectedOutput { get; set; } = "success";
    }

    public async Task<NodeEditorTestResponse> RunTestsAsync(NodeEditorTestRequest request, CancellationToken cancellationToken)
    {
        var logs = new List<string>();
        var cases = new List<NodeEditorTestCaseResult>();

        if (string.IsNullOrWhiteSpace(request.PackageId))
        {
            return Fail("Validation", "PackageId is required.", logs, cases);
        }

        if (string.IsNullOrWhiteSpace(request.ManifestYaml))
        {
            return Fail("Validation", "manifest.yaml content is required.", logs, cases);
        }

        var manifest = ParseManifest(request.ManifestYaml, logs, cases);
        if (manifest == null)
        {
            return new NodeEditorTestResponse(false, logs, cases);
        }

        if (manifest.GetTier() == NodeTier.Compiled && string.IsNullOrWhiteSpace(request.ExecutorCode))
        {
            return Fail("Validation", "Executor source code is required for compiled nodes.", logs, cases);
        }

        var testCases = ParseTests(request.TestsYaml, logs, cases);
        if (testCases == null)
        {
            return new NodeEditorTestResponse(false, logs, cases);
        }

        if (testCases.Count == 0)
        {
            testCases.Add(new TestCaseDocument());
            logs.Add("[SANDBOX] No cases detected. Added one default validation case.");
        }

        var declaredCapabilities = new HashSet<string>(manifest.Capabilities.Select(c => c.Trim().ToLowerInvariant()));
        INodeExecutor? executor = null;
        CollectibleAssemblyLoadContext? loadContext = null;

        try
        {
            if (manifest.GetTier() == NodeTier.Declarative)
            {
                logs.Add("[SANDBOX] Manifest tier is declarative. Skipping Roslyn compilation and using DeclarativeExecutor.");
                executor = new DeclarativeExecutor(manifest.ToDomainManifest(request.PackageId));
            }
            else
            {
                logs.Add("[ROSLYN] Running banned API analyzer.");
                var staticDiagnostics = BannedApiAnalyzer.Analyze(request.ExecutorCode, request.PackageId)
                    .Where(d => string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (staticDiagnostics.Count > 0)
                {
                    foreach (var diagnostic in staticDiagnostics)
                    {
                        logs.Add($"[ANALYZER] {diagnostic.Code}: {diagnostic.Message}");
                    }

                    cases.Add(new NodeEditorTestCaseResult(
                        "Banned API Analyzer Compilation Gate",
                        "fail",
                        staticDiagnostics[0].Message
                    ));
                    return new NodeEditorTestResponse(false, logs, cases);
                }

                logs.Add("[ROSLYN] Compiling executor draft.");
                if (!TryCompileExecutor(request.ExecutorCode, out var assemblyBytes, out var compileErrors))
                {
                    foreach (var err in compileErrors)
                    {
                        logs.Add($"[COMPILER] {err}");
                    }

                    cases.Add(new NodeEditorTestCaseResult(
                        "Compilation",
                        "fail",
                        "Executor compilation failed. Review diagnostics in logs."
                    ));
                    return new NodeEditorTestResponse(false, logs, cases);
                }

                logs.Add("[SANDBOX] Loading compiled assembly into a temporary collectible context.");
                loadContext = new CollectibleAssemblyLoadContext($"NodeEditorSandbox_{Guid.NewGuid():N}");
                var assembly = loadContext.LoadFromBytes(assemblyBytes);
                var executorType = assembly.GetTypes()
                    .FirstOrDefault(t => typeof(INodeExecutor).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

                if (executorType == null)
                {
                    cases.Add(new NodeEditorTestCaseResult(
                        "Executor Discovery",
                        "fail",
                        "No concrete type implementing INodeExecutor was found."
                    ));
                    return new NodeEditorTestResponse(false, logs, cases);
                }

                executor = (INodeExecutor?)Activator.CreateInstance(executorType);
                if (executor == null)
                {
                    cases.Add(new NodeEditorTestCaseResult(
                        "Executor Discovery",
                        "fail",
                        $"Failed to instantiate executor type '{executorType.FullName}'."
                    ));
                    return new NodeEditorTestResponse(false, logs, cases);
                }
            }

            var allPassed = true;
            for (var i = 0; i < testCases.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var testCase = testCases[i];
                var caseName = string.IsNullOrWhiteSpace(testCase.Name) ? $"Case #{i + 1}" : testCase.Name;
                var recorder = new CapabilityRecorder();
                var context = new RecordingNodeContext(recorder);

                try
                {
                    var parameters = testCase.Inputs
                        .ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

                    var result = await executor.ExecuteAsync(new NodeInput(parameters), context, cancellationToken);

                    var undeclared = recorder.Invocations
                        .Where(cap => !declaredCapabilities.Contains(cap))
                        .OrderBy(cap => cap)
                        .ToList();

                    if (undeclared.Count > 0)
                    {
                        allPassed = false;
                        var message = $"Undeclared capability invocation detected: {string.Join(", ", undeclared)}.";
                        cases.Add(new NodeEditorTestCaseResult(caseName, "fail", message));
                        logs.Add($"[SANDBOX] {caseName} failed: {message}");
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(testCase.ExpectedOutput) &&
                        !string.Equals(result.OutputName, testCase.ExpectedOutput, StringComparison.OrdinalIgnoreCase))
                    {
                        allPassed = false;
                        var message = $"Expected output '{testCase.ExpectedOutput}', got '{result.OutputName}'.";
                        cases.Add(new NodeEditorTestCaseResult(caseName, "fail", message));
                        logs.Add($"[SANDBOX] {caseName} failed: {message}");
                        continue;
                    }

                    cases.Add(new NodeEditorTestCaseResult(caseName, "pass", "Outputs and capability usage are valid."));
                    logs.Add($"[SANDBOX] {caseName} passed.");
                }
                catch (Exception ex)
                {
                    allPassed = false;
                    cases.Add(new NodeEditorTestCaseResult(caseName, "fail", ex.Message));
                    logs.Add($"[SANDBOX] {caseName} threw: {ex.Message}");
                }
            }

            logs.Add("[SANDBOX] Unloading temporary sandbox assembly context.");
            return new NodeEditorTestResponse(allPassed, logs, cases);
        }
        finally
        {
            loadContext?.Unload();
        }
    }

    private static ManifestDocument? ParseManifest(string manifestYaml, List<string> logs, List<NodeEditorTestCaseResult> cases)
    {
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var manifest = deserializer.Deserialize<ManifestDocument>(manifestYaml) ?? new ManifestDocument();
            if (manifest.Capabilities == null)
            {
                manifest.Capabilities = new List<string>();
            }

            if (string.IsNullOrWhiteSpace(manifest.Version))
            {
                manifest.Version = "1.0.0";
            }

            logs.Add($"[SANDBOX] Declared capabilities: {JsonSerializer.Serialize(manifest.Capabilities)}");
            return manifest;
        }
        catch (Exception ex)
        {
            logs.Add($"[SANDBOX] manifest.yaml parse failed: {ex.Message}");
            cases.Add(new NodeEditorTestCaseResult("Manifest parse", "fail", ex.Message));
            return null;
        }
    }

    private static List<TestCaseDocument>? ParseTests(string testsYaml, List<string> logs, List<NodeEditorTestCaseResult> cases)
    {
        if (string.IsNullOrWhiteSpace(testsYaml))
        {
            return new List<TestCaseDocument>();
        }

        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var doc = deserializer.Deserialize<TestsDocument>(testsYaml);
            return doc?.Cases ?? new List<TestCaseDocument>();
        }
        catch
        {
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                var directCases = deserializer.Deserialize<List<TestCaseDocument>>(testsYaml);
                return directCases ?? new List<TestCaseDocument>();
            }
            catch (Exception ex)
            {
                logs.Add($"[SANDBOX] tests/cases.yaml parse failed: {ex.Message}");
                cases.Add(new NodeEditorTestCaseResult("Tests parse", "fail", ex.Message));
                return null;
            }
        }
    }

    private static bool TryCompileExecutor(string sourceCode, out byte[] assemblyBytes, out List<string> errors)
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

    private static NodeEditorTestResponse Fail(
        string caseName,
        string message,
        List<string> logs,
        List<NodeEditorTestCaseResult> cases)
    {
        logs.Add($"[VALIDATION] {message}");
        cases.Add(new NodeEditorTestCaseResult(caseName, "fail", message));
        return new NodeEditorTestResponse(false, logs, cases);
    }

    private static TEnum ParseEnumOrDefaultGeneric<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, true, out var parsed) ? parsed : fallback;
    }

    private sealed partial class ManifestDocument
    {
        public NodeTier GetTier()
        {
            return ParseEnumOrDefaultGeneric(Tier, NodeTier.Compiled);
        }

        public NodePackageManifest ToDomainManifest(string fallbackPackageId)
        {
            var packageId = string.IsNullOrWhiteSpace(Id) ? fallbackPackageId : Id;
            var displayName = string.IsNullOrWhiteSpace(DisplayName) ? packageId : DisplayName;

            return new NodePackageManifest(
                new NodePackageId(packageId),
                string.IsNullOrWhiteSpace(Version) ? "1.0.0" : Version,
                displayName,
                string.IsNullOrWhiteSpace(Category) ? "Utility" : Category,
                GetTier(),
                ParseEnumOrDefaultGeneric(SideEffectKindName, NodeSideEffectKind.IdempotentSideEffect),
                ParseEnumOrDefaultGeneric(RecoveryModeName, Knotarium.Core.Domain.RecoveryMode.FailImmediately),
                DefaultTimeoutSeconds,
                Capabilities ?? new List<string>(),
                Parameters?.Select(p => new ParameterDefinition(p.Name, p.Type, p.Required, p.Expression, p.Values)).ToList() ?? new List<ParameterDefinition>(),
                Outputs?.Where(o => !string.IsNullOrWhiteSpace(o.Name)).Select(o => new OutputDefinition(o.Name)).ToList() ?? new List<OutputDefinition>()
            );
        }
    }

    private sealed class CapabilityRecorder
    {
        private readonly HashSet<string> _invocations = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> Invocations => _invocations;

        public void Record(string capability)
        {
            if (!string.IsNullOrWhiteSpace(capability))
            {
                _invocations.Add(capability.Trim().ToLowerInvariant());
            }
        }
    }

    private sealed class RecordingNodeContext : INodeContext
    {
        public ILogger Logger { get; }
        public IWorkflowState State { get; }
        public IHttpClient? Http { get; }
        public ICredentialAccessor? Credentials { get; }

        public RecordingNodeContext(CapabilityRecorder recorder)
        {
            Logger = new RecordingLogger(recorder);
            State = new RecordingWorkflowState();
            Http = new RecordingHttpClient(recorder);
            Credentials = new RecordingCredentialAccessor(recorder);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly CapabilityRecorder _recorder;

        public RecordingLogger(CapabilityRecorder recorder)
        {
            _recorder = recorder;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _recorder.Record("logging");
        }
    }

    private sealed class RecordingWorkflowState : IWorkflowState
    {
        private readonly Dictionary<string, object?> _variables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(NodeId NodeId, string Output), JsonElement> _outputs = new();

        public T? GetVariable<T>(string name)
        {
            if (_variables.TryGetValue(name, out var value) && value is T typed)
            {
                return typed;
            }

            return default;
        }

        public void SetVariable(string name, object? value)
        {
            _variables[name] = value;
        }

        public JsonElement? GetNodeOutput(NodeId nodeId, string outputName)
        {
            return _outputs.TryGetValue((nodeId, outputName), out var output) ? output : null;
        }
    }

    private sealed class RecordingHttpClient : IHttpClient
    {
        private readonly CapabilityRecorder _recorder;

        public RecordingHttpClient(CapabilityRecorder recorder)
        {
            _recorder = recorder;
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _recorder.Record("http");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }

    private sealed class RecordingCredentialAccessor : ICredentialAccessor
    {
        private readonly CapabilityRecorder _recorder;

        public RecordingCredentialAccessor(CapabilityRecorder recorder)
        {
            _recorder = recorder;
        }

        public Task<string?> GetSecretAsync(string credentialRef, CancellationToken cancellationToken = default)
        {
            _recorder.Record("credentials");
            return Task.FromResult<string?>("sandbox-secret");
        }
    }

    private sealed class AsyncNoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
