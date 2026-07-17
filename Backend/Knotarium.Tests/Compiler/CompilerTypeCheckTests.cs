// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Knotarium.Tests.Compiler;

public class TypeCompatibilityTests
{
    [Theory]
    [InlineData("any", "number", true)]
    [InlineData("object", "any", true)]
    [InlineData("number", "number", true)]
    [InlineData("number", "string", true)]   // string is the universal sink
    [InlineData("boolean", "string", true)]
    [InlineData("object", "string", true)]
    [InlineData("string", "number", true)]   // a string may carry a number
    [InlineData("string", "boolean", true)]
    [InlineData("object", "number", false)]  // clear mismatch
    [InlineData("array", "number", false)]
    [InlineData("object", "array", false)]
    [InlineData("number", "object", false)]
    public void IsAssignable_FollowsLattice(string from, string to, bool expected)
    {
        Assert.Equal(expected, TypeCompatibility.IsAssignable(from, to));
    }

    [Theory]
    [InlineData("int", "number")]
    [InlineData("integer", "number")]
    [InlineData("bool", "boolean")]
    [InlineData("json", "object")]
    [InlineData("list", "array")]
    [InlineData("enum", "string")]
    [InlineData("credentialRef", "string")]
    [InlineData("", "any")]
    [InlineData(null, "any")]
    [InlineData("somethingCustom", "any")]
    public void Normalize_CollapsesAliases(string? input, string expected)
    {
        Assert.Equal(expected, TypeCompatibility.Normalize(input));
    }

    [Theory]
    [InlineData("any", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("object", true)]
    [InlineData("number", true)]
    public void IsKnown_TreatsAnyAsUnknown(string? type, bool expected)
    {
        Assert.Equal(expected, TypeCompatibility.IsKnown(type));
    }
}

public class CompilerTypeCheckTests
{
    private readonly MockWorkflowDefinitionProvider _provider = new();
    private readonly InMemoryNodePackageManifestProvider _manifestProvider = new();
    private readonly WorkflowCompiler _compiler;

    public CompilerTypeCheckTests()
    {
        _compiler = new WorkflowCompiler(_provider, _manifestProvider);
    }

    private static WorkflowDefinition Flow(EdgeDefinition httpToDelayEdge)
    {
        var start = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var http = new NodeDefinition(NodeId.Create("http"), "httpRequest", new Dictionary<string, object>());
        var delay = new NodeDefinition(NodeId.Create("delay"), "delay", new Dictionary<string, object>());

        return new WorkflowDefinition(
            WorkflowDefinitionId.New(),
            "Type-check flow",
            new[] { start, http, delay },
            new[]
            {
                new EdgeDefinition("e1", start.Id, "result", http.Id, "in"),
                httpToDelayEdge,
            });
    }

    [Fact]
    public async Task ObjectOutput_IntoNumberInput_WarnsButStillCompiles()
    {
        // httpRequest.success (object) -> delay.delayMs (number): a clear mismatch.
        var edge = new EdgeDefinition("e2", NodeId.Create("http"), "success", NodeId.Create("delay"), "delayMs");
        var result = await _compiler.CompileAsync(Flow(edge));

        // Non-blocking: warnings don't fail compilation.
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Plan);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var warning = Assert.Single(result.Diagnostics, d => d.Code == "WARN_TYPE_MISMATCH");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("e2", warning.EdgeId);
    }

    [Fact]
    public async Task StringOutput_IntoNumberInput_DoesNotWarn()
    {
        // httpRequest.error (string) -> delay.delayMs (number): a string may carry a number, so
        // the lenient lattice allows it (no false positive).
        var edge = new EdgeDefinition("e2", NodeId.Create("http"), "error", NodeId.Create("delay"), "delayMs");
        var result = await _compiler.CompileAsync(Flow(edge));

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "WARN_TYPE_MISMATCH");
    }

    [Fact]
    public async Task GenericPayloadInput_IsNotTypeChecked()
    {
        // httpRequest.success (object) -> delay."in" (wildcard data socket, no declared type):
        // treated as "any" and skipped. Field-level safety for these is Phase B.
        var edge = new EdgeDefinition("e2", NodeId.Create("http"), "success", NodeId.Create("delay"), "in");
        var result = await _compiler.CompileAsync(Flow(edge));

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "WARN_TYPE_MISMATCH");
    }

    [Fact]
    public async Task PlainWorkflow_ProducesNoTypeWarnings()
    {
        // The common path (untyped passthrough into "in") must stay quiet.
        var start = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var log = new NodeDefinition(NodeId.Create("log"), "log", new Dictionary<string, object> { ["message"] = "hi" });
        var end = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(
            WorkflowDefinitionId.New(),
            "Plain",
            new[] { start, log, end },
            new[]
            {
                new EdgeDefinition("e1", start.Id, "result", log.Id, "in"),
                new EdgeDefinition("e2", log.Id, "result", end.Id, "in"),
            });

        var result = await _compiler.CompileAsync(workflow);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "WARN_TYPE_MISMATCH");
    }
}
