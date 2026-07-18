// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Core.Contracts;
using Knotarium.Features.NodeEditor;
using Xunit;

namespace Knotarium.Tests.Api;

/// <summary>
/// Security regression for the node-editor sandbox: testing a compiled node compiles and runs its
/// executor as real in-process code, so it must be gated by the same off-by-default CodeExecution
/// capability as the inline-code and compiled-node tasks — the banned-API analyzer is authoring UX,
/// not the security boundary. Fail-closed when the capability is off.
/// </summary>
public sealed class NodeEditorSandboxCapabilityTests
{
    private sealed class FakeCapabilities : ICapabilityPolicy
    {
        private readonly bool _enabled;
        public FakeCapabilities(bool enabled) => _enabled = enabled;
        public Task<bool> IsEnabledAsync(string capability, CancellationToken cancellationToken = default) => Task.FromResult(_enabled);
    }

    // A manifest with no explicit tier defaults to Compiled (SandboxDocuments), which is the path that
    // compiles + runs C#.
    private const string CompiledManifest = "name: Test Node\ntier: Compiled\n";

    private static NodeEditorTestRequest Request(string executorCode = "public class E {}") =>
        new("test-pkg", CompiledManifest, executorCode, TestsYaml: "");

    [Fact]
    public async Task Compiled_test_is_refused_when_code_execution_capability_is_off()
    {
        var sut = new NodeEditorSandboxService(new FakeCapabilities(enabled: false));

        var response = await sut.RunTestsAsync(Request(), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains(response.Cases, c => c.Message.Contains("code execution", System.StringComparison.OrdinalIgnoreCase));
        // Fail-closed BEFORE any compilation/execution: no compile diagnostics should appear.
        Assert.DoesNotContain(response.Logs, l => l.Contains("Roslyn", System.StringComparison.OrdinalIgnoreCase));
    }

    // Interpreted is the OTHER non-declarative tier: it also reaches the compile-and-run path, so it
    // must be gated too. Regression against a fail-open `== Compiled` check that let it through ungated.
    private const string InterpretedManifest = "name: Test Node\ntier: Interpreted\n";

    [Fact]
    public async Task Interpreted_test_is_refused_when_code_execution_capability_is_off()
    {
        var sut = new NodeEditorSandboxService(new FakeCapabilities(enabled: false));

        var response = await sut.RunTestsAsync(
            new("test-pkg", InterpretedManifest, "public class E {}", TestsYaml: ""), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains(response.Cases, c => c.Message.Contains("code execution", System.StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(response.Logs, l => l.Contains("Roslyn", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Compiled_test_proceeds_past_the_gate_when_capability_is_on()
    {
        var sut = new NodeEditorSandboxService(new FakeCapabilities(enabled: true));

        var response = await sut.RunTestsAsync(Request(), CancellationToken.None);

        // It may still fail later (the trivial executor implements no INodeExecutor), but NOT because of
        // the capability gate — the fix must not block execution when the capability is enabled.
        Assert.DoesNotContain(response.Cases,
            c => c.Message.Contains("code execution", System.StringComparison.OrdinalIgnoreCase));
    }
}
