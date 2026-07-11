using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Knotarium.Core.Exceptions;
using Knotarium.Infrastructure.OpenApi;
using Xunit;

namespace Knotarium.Tests.OpenApi;

public class ParserAdapterTests
{
    private static readonly MicrosoftOpenApiParser Parser = new();

    private static ReadOnlyMemory<byte> LoadFixture(string name)
    {
        var asm = typeof(ParserAdapterTests).Assembly;
        var resourceName = $"Knotarium.Tests.OpenApi.Fixtures.{name}";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Fixture '{name}' not found. Available: {string.Join(", ", asm.GetManifestResourceNames())}");
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // -------------------------------------------------------------------------
    // Format detection
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("petstore-swagger20.json", "swagger2.0")]
    [InlineData("petstore-swagger20.yaml", "swagger2.0")]
    [InlineData("petstore-openapi30.json", "openapi3.0")]
    [InlineData("petstore-openapi30.yaml", "openapi3.0")]
    [InlineData("petstore-openapi31.json", "openapi3.1")]
    public async Task Parse_KnownFormats_ReturnsCorrectOriginalFormat(string fixture, string expectedFormat)
    {
        var result = await Parser.ParseAsync(LoadFixture(fixture));
        Assert.Equal(expectedFormat, result.Metadata.OriginalFormat);
    }

    // -------------------------------------------------------------------------
    // Operations
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("petstore-swagger20.json")]
    [InlineData("petstore-swagger20.yaml")]
    [InlineData("petstore-openapi30.json")]
    [InlineData("petstore-openapi30.yaml")]
    [InlineData("petstore-openapi31.json")]
    public async Task Parse_AllFormats_ReturnsAtLeastOneOperation(string fixture)
    {
        var result = await Parser.ParseAsync(LoadFixture(fixture));
        Assert.NotEmpty(result.Operations);
    }

    // -------------------------------------------------------------------------
    // OpenAPI 3.1.0 support (added with Microsoft.OpenApi v2.x upgrade)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Parse_OpenApi31_Succeeds()
    {
        var result = await Parser.ParseAsync(LoadFixture("petstore-openapi31.json"));
        Assert.NotEmpty(result.Operations);
        Assert.Equal("openapi3.1", result.Metadata.OriginalFormat);
    }

    // -------------------------------------------------------------------------
    // External $ref
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Parse_ExternalRef_ThrowsOpenApiParseException()
    {
        var ex = await Assert.ThrowsAsync<OpenApiParseException>(
            () => Parser.ParseAsync(LoadFixture("external-ref.yaml")));
        Assert.Contains("External $ref", ex.Message);
    }

    // -------------------------------------------------------------------------
    // Internal $ref
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Parse_InternalRef_Succeeds()
    {
        var result = await Parser.ParseAsync(LoadFixture("internal-ref.yaml"));
        Assert.NotEmpty(result.Operations);
        Assert.NotEmpty(result.Schemas);
    }

    // -------------------------------------------------------------------------
    // Tags
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Parse_NoTags_OperationsHaveEmptyTagList()
    {
        var result = await Parser.ParseAsync(LoadFixture("minimal-no-tags.yaml"));
        Assert.All(result.Operations, op => Assert.Empty(op.Tags));
    }

    // -------------------------------------------------------------------------
    // Swagger 2.0 server normalization
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Parse_Swagger20_DefaultServersContainsUrl()
    {
        var result = await Parser.ParseAsync(LoadFixture("petstore-swagger20.json"));
        Assert.NotEmpty(result.Metadata.DefaultServers);
        Assert.All(result.Metadata.DefaultServers, s => Assert.False(string.IsNullOrEmpty(s)));
    }

    // -------------------------------------------------------------------------
    // Invalid content
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Parse_InvalidContent_ThrowsOpenApiParseException()
    {
        var garbage = Encoding.UTF8.GetBytes("not yaml or json {{{{");
        await Assert.ThrowsAsync<OpenApiParseException>(
            () => Parser.ParseAsync(garbage));
    }

    // -------------------------------------------------------------------------
    // OperationId fallback
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Parse_MissingOperationId_GeneratesFallback()
    {
        var result = await Parser.ParseAsync(LoadFixture("minimal-no-tags.yaml"));
        Assert.All(result.Operations, op => Assert.False(string.IsNullOrEmpty(op.OperationId)));
    }
}
