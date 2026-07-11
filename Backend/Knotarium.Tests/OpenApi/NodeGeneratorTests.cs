using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.OpenApi;
using Knotarium.Core.Domain.OpenApi;
using Knotarium.Features.OpenApi;
using Xunit;

namespace Knotarium.Tests.OpenApi;

public class NodeGeneratorTests
{
    private readonly OpenApiNodeGenerator _generator = new();

    private static ParsedSpec BuildSpec(string id, string title, params string[] operationIds)
    {
        var ops = operationIds
            .Select(oid => new ApiOperation(oid, "GET", "/x", null,
                Array.Empty<string>(), Array.Empty<ApiParameter>(), null, Array.Empty<string>()))
            .ToList();

        return new ParsedSpec(
            new ImportedSpec(new OpenApiSpecId(id), title, "1.0", "openapi3.0",
                Array.Empty<string>(), Array.Empty<string>(), DateTimeOffset.UtcNow, 1),
            ops,
            Array.Empty<ApiSchema>(),
            Array.Empty<SecurityScheme>());
    }

    [Fact]
    public void Generate_PackageId_IsPrefixedWithOpenApi()
    {
        var pkg = _generator.Generate(BuildSpec("petstore", "Petstore"));
        Assert.StartsWith("openapi.", pkg.PackageId);
    }

    [Fact]
    public void Generate_ManifestYaml_ContainsAllOperationIds()
    {
        var pkg = _generator.Generate(BuildSpec("api", "My API", "getUser", "listUsers", "deleteUser"));
        Assert.Contains("getUser", pkg.ManifestYaml);
        Assert.Contains("listUsers", pkg.ManifestYaml);
        Assert.Contains("deleteUser", pkg.ManifestYaml);
    }

    [Fact]
    public void Generate_ManifestYaml_DisplayNameMatchesSpecTitle()
    {
        var pkg = _generator.Generate(BuildSpec("api", "Petstore"));
        Assert.Contains("Petstore", pkg.ManifestYaml);
    }

    [Fact]
    public void Generate_ManifestYaml_CategoryIsIntegrations()
    {
        var pkg = _generator.Generate(BuildSpec("api", "Test"));
        Assert.Contains("Integrations", pkg.ManifestYaml);
    }

    [Fact]
    public void Generate_ManifestYaml_TierIsInterpreted()
    {
        var pkg = _generator.Generate(BuildSpec("api", "Test"));
        Assert.Contains("tier: Interpreted", pkg.ManifestYaml);
        Assert.DoesNotContain("tier: Compiled", pkg.ManifestYaml);
    }

    [Fact]
    public void Generate_ManifestJson_TierIsInterpreted()
    {
        var json = _generator.GenerateManifestJson(BuildSpec("api", "Test"));
        Assert.Contains("\"tier\":\"Interpreted\"", json);
    }

    [Fact]
    public void Generate_ManifestYaml_HasServerConfigIdParam()
    {
        var pkg = _generator.Generate(BuildSpec("api", "Test"));
        Assert.Contains("serverConfigId", pkg.ManifestYaml);
    }

    [Fact]
    public void Generate_ManifestYaml_HasArgumentsParam_WithExpression()
    {
        var pkg = _generator.Generate(BuildSpec("api", "Test", "op1"));
        var yaml = pkg.ManifestYaml;

        // Find "arguments" parameter and verify expression: true follows
        var argsIdx = yaml.IndexOf("name: arguments", StringComparison.Ordinal);
        Assert.True(argsIdx >= 0, "arguments parameter not found");

        var snippet = yaml[argsIdx..];
        Assert.Contains("expression: true", snippet);
    }

    [Fact]
    public void Generate_EmitsNoExecutorSource()
    {
        // Option C: openapi.* packages run through the pre-compiled interpreter, so the
        // generator emits the manifest only — never per-spec C# source to compile.
        var pkg = _generator.Generate(BuildSpec("petstore", "Petstore"));
        Assert.True(string.IsNullOrEmpty(pkg.ExecutorCode));
    }

    [Fact]
    public void Generate_TwoCallsSameSpec_ProduceSameOutput()
    {
        var spec = BuildSpec("api", "My API", "op1");
        var a = _generator.Generate(spec);
        var b = _generator.Generate(spec);
        Assert.Equal(a.ManifestYaml, b.ManifestYaml);
        Assert.Equal(a.ExecutorCode, b.ExecutorCode);
    }

    [Fact]
    public void Generate_SpecWithNoOperations_ManifestHasEmptyValues()
    {
        var pkg = _generator.Generate(BuildSpec("api", "Empty API"));
        Assert.Contains("values: []", pkg.ManifestYaml);
    }

    [Fact]
    public void Generate_SpecId_IsNormalized()
    {
        var pkg = _generator.Generate(BuildSpec("my-api!@#$%", "My API!!"));
        // PackageId should only contain alphanumeric, dots, and hyphens
        Assert.Matches(@"^[a-z0-9.\-]+$", pkg.PackageId);
    }
}
