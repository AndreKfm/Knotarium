using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Knotarium.Tests.Api;

[Collection(WorkflowExecutionIsolationCollection.Name)]
public sealed class OpenApiApiTests : IClassFixture<KnotariumApiFactory>, IDisposable
{
    private static readonly byte[] TestPrivateKey = Enumerable.Range(1, 32).Select(v => (byte)v).ToArray();
    private static readonly string TestPublicKey = Convert.ToBase64String(PackageSigner.DerivePublicKey(TestPrivateKey));

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _databasePath;
    private readonly string _tempWorkflowStoreFolder;
    private readonly HttpClient _client;

    public OpenApiApiTests(KnotariumApiFactory factory)
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"knotarium-openapi-tests-{Guid.NewGuid():N}.db");
        _tempWorkflowStoreFolder = Path.Combine(Path.GetTempPath(), $"knotarium-openapi-wf-{Guid.NewGuid():N}");

        var connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null) services.Remove(descriptor);

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

                var writerDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(Knotarium.Core.Contracts.IExecutionJournalWriter));
                if (writerDescriptor != null) services.Remove(writerDescriptor);
                services.AddScoped<Knotarium.Core.Contracts.IExecutionJournalWriter>(_ => new Knotarium.Infrastructure.Persistence.SqliteExecutionJournalWriter(connectionString));

                var fileStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(FileWorkflowStore));
                if (fileStoreDescriptor != null) services.Remove(fileStoreDescriptor);
                var storeDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(Knotarium.Core.Contracts.IWorkflowStore));
                if (storeDescriptor != null) services.Remove(storeDescriptor);
                services.AddScoped(sp => new FileWorkflowStore(_tempWorkflowStoreFolder, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileWorkflowStore>>()));
                services.AddScoped<Knotarium.Core.Contracts.IWorkflowStore>(sp => sp.GetRequiredService<FileWorkflowStore>());
            });

            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:PackageSigning:TrustedPublicKeys:0"] = TestPublicKey,
                    ["Security:PackageSigning:HostPrivateKeyBase64"] = Convert.ToBase64String(TestPrivateKey),
                    ["Security:Credentials:EncryptionKeyBase64"] = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA="
                });
            });
        });

        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_databasePath)) File.Delete(_databasePath); } catch { }
        try { if (Directory.Exists(_tempWorkflowStoreFolder)) Directory.Delete(_tempWorkflowStoreFolder, true); } catch { }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static byte[] LoadFixture(string name)
    {
        var asm = typeof(OpenApiApiTests).Assembly;
        var resourceName = $"Knotarium.Tests.Api.OpenApi.Fixtures.{name}";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Fixture '{name}' not found. Available: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> PostFixture(string fixtureName, string mediaType = "application/json")
    {
        var bytes = LoadFixture(fixtureName);
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) } }, "file", fixtureName);
        var response = await _client.PostAsync("/api/openapi/specs", content);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response, body);
    }

    private async Task<string> ImportAndGetId(string fixtureName)
    {
        var (resp, body) = await PostFixture(fixtureName);
        resp.EnsureSuccessStatusCode();
        return body.GetProperty("id").GetString()!;
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> PostFixtureWithId(string fixtureName, string specId, string mediaType = "application/json")
    {
        var bytes = LoadFixture(fixtureName);
        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) } }, "file", fixtureName },
            { new StringContent(specId), "specId" },
        };
        var response = await _client.PostAsync("/api/openapi/specs", content);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response, body);
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Import_ValidOpenApi30Json_Returns200WithId()
    {
        var (response, body) = await PostFixture("petstore-openapi30.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("id").GetString()));
        Assert.Equal(1, body.GetProperty("versionNumber").GetInt32());
    }

    [Fact]
    public async Task Import_ValidSwagger20Yaml_Returns200()
    {
        var (response, body) = await PostFixture("petstore-swagger20.yaml", "text/yaml");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("swagger2.0", body.GetProperty("originalFormat").GetString());
    }

    [Fact]
    public async Task Import_ExternalRef_Returns400()
    {
        var (response, body) = await PostFixture("external-ref.yaml", "text/yaml");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("External $ref", body.GetProperty("message").GetString() ?? "");
    }

    [Fact]
    public async Task Import_InvalidContent_Returns400()
    {
        var bytes = Encoding.UTF8.GetBytes("not yaml or json {{{{");
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes), "file", "garbage.txt");
        var response = await _client.PostAsync("/api/openapi/specs", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_SameExplicitId_VersionIncrements()
    {
        var (r1, first) = await PostFixtureWithId("petstore-openapi30.json", "my-api");
        r1.EnsureSuccessStatusCode();
        var (r2, second) = await PostFixtureWithId("petstore-openapi30.json", "my-api");
        r2.EnsureSuccessStatusCode();

        // Same explicit id → second import is version 2 of the same spec.
        Assert.Equal("my-api", first.GetProperty("id").GetString());
        Assert.Equal("my-api", second.GetProperty("id").GetString());
        Assert.Equal(1, first.GetProperty("versionNumber").GetInt32());
        Assert.Equal(2, second.GetProperty("versionNumber").GetInt32());
    }

    [Fact]
    public async Task Import_DistinctExplicitIds_StayDistinct()
    {
        // Two imports that would otherwise collide on title slug are kept apart by explicit ids.
        var (r1, first) = await PostFixtureWithId("petstore-openapi30.json", "tenant-a");
        r1.EnsureSuccessStatusCode();
        var (r2, second) = await PostFixtureWithId("petstore-openapi30.json", "tenant-b");
        r2.EnsureSuccessStatusCode();

        Assert.Equal("tenant-a", first.GetProperty("id").GetString());
        Assert.Equal("tenant-b", second.GetProperty("id").GetString());
        Assert.Equal(1, first.GetProperty("versionNumber").GetInt32());
        Assert.Equal(1, second.GetProperty("versionNumber").GetInt32());
    }

    [Fact]
    public async Task Import_BlankExplicitId_FallsBackToTitleSlug()
    {
        // Empty specId is ignored; identity falls back to the title slug.
        var (resp, body) = await PostFixtureWithId("petstore-openapi30.json", "   ");
        resp.EnsureSuccessStatusCode();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task List_AfterTwoImports_ReturnsBoth()
    {
        // Both fixtures share the title "Petstore"; distinct explicit ids keep them apart
        // (otherwise they collide on the title slug into a single versioned spec).
        await PostFixtureWithId("petstore-openapi30.json", "petstore-a");
        await PostFixtureWithId("petstore-swagger20.json", "petstore-b");

        var response = await _client.GetAsync("/api/openapi/specs");
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.NotNull(list);
        Assert.True(list!.Length >= 2);
    }

    [Fact]
    public async Task Delete_RemovesSpecAndGeneratedNodePackage()
    {
        // Explicit id makes the generated package id deterministic: "openapi.del-test".
        var (importResp, _) = await PostFixtureWithId("petstore-openapi30.json", "del-test");
        importResp.EnsureSuccessStatusCode();

        async Task<bool> PackageExists()
        {
            var resp = await _client.GetAsync("/api/node-packages");
            resp.EnsureSuccessStatusCode();
            var packages = await resp.Content.ReadFromJsonAsync<JsonElement[]>();
            return packages!.Any(p => p.GetProperty("id").GetString() == "openapi.del-test");
        }

        Assert.True(await PackageExists(), "Imported spec should add an openapi.* node package to the palette.");

        var delete = await _client.DeleteAsync("/api/openapi/specs/del-test");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        // Spec gone …
        var specResp = await _client.GetAsync("/api/openapi/specs/del-test");
        Assert.Equal(HttpStatusCode.NotFound, specResp.StatusCode);

        // … and the generated node package gone with it (no orphan in the palette).
        Assert.False(await PackageExists(), "Deleting the spec must also remove its generated node package.");
    }

    [Fact]
    public async Task GetById_ExistingSpec_ReturnsGroupedModel()
    {
        var id = await ImportAndGetId("petstore-openapi30.json");

        var response = await _client.GetAsync($"/api/openapi/specs/{id}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.NotEmpty(body.GetProperty("groups").EnumerateArray());
    }

    [Fact]
    public async Task GetById_UnknownId_Returns404()
    {
        var response = await _client.GetAsync("/api/openapi/specs/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetVersions_AfterOneImport_ReturnsOne()
    {
        var id = await ImportAndGetId("petstore-openapi30.json");

        var response = await _client.GetAsync($"/api/openapi/specs/{id}/versions");
        response.EnsureSuccessStatusCode();
        var versions = await response.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.NotNull(versions);
        Assert.Single(versions!);
    }

    [Fact]
    public async Task GetOperation_KnownOperationId_Returns200()
    {
        var id = await ImportAndGetId("petstore-openapi30.json");

        var response = await _client.GetAsync($"/api/openapi/specs/{id}/operations/listPets");
        response.EnsureSuccessStatusCode();
        var op = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("listPets", op.GetProperty("operationId").GetString());
    }

    [Fact]
    public async Task GetOperation_UnknownOperationId_Returns404()
    {
        var id = await ImportAndGetId("petstore-openapi30.json");

        var response = await _client.GetAsync($"/api/openapi/specs/{id}/operations/doesNotExist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
