using System;
using System.Collections.Generic;
using System.Linq;
using KnotGarden.Core.Contracts.OpenApi;
using KnotGarden.Core.Domain.OpenApi;
using KnotGarden.Features.OpenApi;
using Xunit;

namespace KnotGarden.Tests.OpenApi;

public class ResourceLocatorInferenceTests
{
    private static ApiOperation Op(string operationId, string method, string path, params ApiParameter[] parameters) =>
        new(operationId, method, path, null, Array.Empty<string>(), parameters, null, Array.Empty<string>());

    private static ApiParameter PathParam(string name) => new(name, "path", true, null, "{\"type\":\"string\"}");

    private static ParsedSpec Spec(params ApiOperation[] ops) =>
        new(new ImportedSpec(new OpenApiSpecId("s"), "S", "1.0", "openapi3.0",
                Array.Empty<string>(), Array.Empty<string>(), DateTimeOffset.UtcNow, 1),
            ops, Array.Empty<ApiSchema>(), Array.Empty<SecurityScheme>());

    [Fact]
    public void Suggest_SimpleParentCollection_Detected()
    {
        var getById = Op("getPet", "GET", "/pets/{id}", PathParam("id"));
        var list = Op("listPets", "GET", "/pets");
        var spec = Spec(getById, list);

        var suggestions = ResourceLocatorInference.Suggest(spec, getById);

        var s = Assert.Single(suggestions);
        Assert.Equal("id", s.Name);
        Assert.Equal("/pets", s.CollectionPath);
        Assert.Equal("id", s.ValueField);
        Assert.Equal("name", s.LabelField);
        Assert.Empty(s.DependsOn);
    }

    [Fact]
    public void Suggest_NoSiblingCollection_NoSuggestion()
    {
        var getById = Op("getPet", "GET", "/pets/{id}", PathParam("id"));
        var spec = Spec(getById); // no GET /pets

        Assert.Empty(ResourceLocatorInference.Suggest(spec, getById));
    }

    [Fact]
    public void Suggest_NestedPath_MarksCascadingDependency()
    {
        var getNested = Op("getStorePet", "GET", "/stores/{storeId}/pets/{petId}", PathParam("storeId"), PathParam("petId"));
        var listStores = Op("listStores", "GET", "/stores");
        var listStorePets = Op("listStorePets", "GET", "/stores/{storeId}/pets", PathParam("storeId"));
        var spec = Spec(getNested, listStores, listStorePets);

        var suggestions = ResourceLocatorInference.Suggest(spec, getNested);

        var storeId = suggestions.Single(s => s.Name == "storeId");
        Assert.Equal("/stores", storeId.CollectionPath);
        Assert.Empty(storeId.DependsOn);

        var petId = suggestions.Single(s => s.Name == "petId");
        Assert.Equal("/stores/{storeId}/pets", petId.CollectionPath);
        Assert.Equal(new[] { "storeId" }, petId.DependsOn);
    }

    [Fact]
    public void Suggest_QueryParam_Ignored()
    {
        var op = Op("listPets", "GET", "/pets", new ApiParameter("status", "query", false, null, "{}"));
        Assert.Empty(ResourceLocatorInference.Suggest(Spec(op), op));
    }
}
