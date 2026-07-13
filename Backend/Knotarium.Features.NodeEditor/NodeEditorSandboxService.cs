using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.NodeRuntime;

namespace Knotarium.Features.NodeEditor;

/// <summary>
/// Runs node-editor draft tests: validates the request, obtains an executor
/// (declarative, or compiled via <see cref="SandboxExecutorCompiler"/> after the banned-API
/// gate), then executes each case inside a <see cref="RecordingNodeContext"/> and fails
/// cases that invoke capabilities the manifest never declared.
/// </summary>
public sealed class NodeEditorSandboxService : INodeEditorSandboxService
{
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

        var manifest = SandboxYamlParser.ParseManifest(request.ManifestYaml, logs, cases);
        if (manifest == null)
        {
            return new NodeEditorTestResponse(false, logs, cases);
        }

        if (manifest.GetTier() == NodeTier.Compiled && string.IsNullOrWhiteSpace(request.ExecutorCode))
        {
            return Fail("Validation", "Executor source code is required for compiled nodes.", logs, cases);
        }

        var testCases = SandboxYamlParser.ParseTests(request.TestsYaml, logs, cases);
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
        CollectibleAssemblyLoadContext? loadContext = null;

        try
        {
            INodeExecutor? executor;
            if (manifest.GetTier() == NodeTier.Declarative)
            {
                logs.Add("[SANDBOX] Manifest tier is declarative. Skipping Roslyn compilation and using DeclarativeExecutor.");
                executor = new DeclarativeExecutor(manifest.ToDomainManifest(request.PackageId));
            }
            else
            {
                (executor, loadContext) = CompileExecutor(request, logs, cases);
                if (executor == null)
                {
                    return new NodeEditorTestResponse(false, logs, cases);
                }
            }

            var allPassed = await RunTestCasesAsync(executor, testCases, declaredCapabilities, logs, cases, cancellationToken);

            logs.Add("[SANDBOX] Unloading temporary sandbox assembly context.");
            return new NodeEditorTestResponse(allPassed, logs, cases);
        }
        finally
        {
            loadContext?.Unload();
        }
    }

    /// <summary>
    /// Runs the banned-API gate, compiles the draft, and instantiates the executor.
    /// Returns a null executor (with a failed case recorded) when any stage rejects the
    /// draft; the load context, once created, is always returned so the caller can unload it.
    /// </summary>
    private static (INodeExecutor? Executor, CollectibleAssemblyLoadContext? LoadContext) CompileExecutor(
        NodeEditorTestRequest request,
        List<string> logs,
        List<NodeEditorTestCaseResult> cases)
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
            return (null, null);
        }

        logs.Add("[ROSLYN] Compiling executor draft.");
        if (!SandboxExecutorCompiler.TryCompileExecutor(request.ExecutorCode, out var assemblyBytes, out var compileErrors))
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
            return (null, null);
        }

        logs.Add("[SANDBOX] Loading compiled assembly into a temporary collectible context.");
        var loadContext = new CollectibleAssemblyLoadContext($"NodeEditorSandbox_{Guid.NewGuid():N}");
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
            return (null, loadContext);
        }

        var executor = (INodeExecutor?)Activator.CreateInstance(executorType);
        if (executor == null)
        {
            cases.Add(new NodeEditorTestCaseResult(
                "Executor Discovery",
                "fail",
                $"Failed to instantiate executor type '{executorType.FullName}'."
            ));
            return (null, loadContext);
        }

        return (executor, loadContext);
    }

    private static async Task<bool> RunTestCasesAsync(
        INodeExecutor executor,
        List<TestCaseDocument> testCases,
        HashSet<string> declaredCapabilities,
        List<string> logs,
        List<NodeEditorTestCaseResult> cases,
        CancellationToken cancellationToken)
    {
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

        return allPassed;
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
}
