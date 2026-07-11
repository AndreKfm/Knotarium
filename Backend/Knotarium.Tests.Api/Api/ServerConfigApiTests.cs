using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
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
public sealed class ServerConfigApiTests : IClassFixture<KnotariumApiFactory>, IDisposable
{
    private static readonly byte[] TestPrivateKey = Enumerable.Range(1, 32).Select(v => (byte)v).ToArray();
    private static readonly string TestPublicKey = Convert.ToBase64String(PackageSigner.DerivePublicKey(TestPrivateKey));

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _databasePath;
    private readonly string _tempWorkflowStoreFolder;
    private readonly HttpClient _client;

    public ServerConfigApiTests(KnotariumApiFactory factory)
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"knotarium-servercfg-tests-{Guid.NewGuid():N}.db");
        _tempWorkflowStoreFolder = Path.Combine(Path.GetTempPath(), $"knotarium-servercfg-wf-{Guid.NewGuid():N}");

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

    private static object ValidPayload(string name = "Test", string baseUrl = "https://api.example.com", string? credentialRef = null) =>
        new { name, baseUrl, serverVariables = (object?)null, securitySchemeType = "none", credentialRef };

    private async Task<(HttpResponseMessage Response, JsonElement Body)> Post(object payload)
    {
        var response = await _client.PostAsJsonAsync("/api/server-configs", payload);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response, body);
    }

    private async Task<string> CreateAndGetId(string name = "Test", string baseUrl = "https://api.example.com")
    {
        var (resp, body) = await Post(ValidPayload(name, baseUrl));
        resp.EnsureSuccessStatusCode();
        return body.GetProperty("id").GetString()!;
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Create_ValidConfig_Returns201WithId()
    {
        var (response, body) = await Post(ValidPayload());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Create_MissingName_Returns400()
    {
        var (response, _) = await Post(new { name = "", baseUrl = "https://api.example.com", securitySchemeType = "none" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_MissingBaseUrl_Returns400()
    {
        var (response, _) = await Post(new { name = "Test", baseUrl = "", securitySchemeType = "none" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidCredentialRef_Returns400()
    {
        var (response, body) = await Post(new { name = "Test", baseUrl = "https://api.example.com", securitySchemeType = "apiKey", credentialRef = "does-not-exist" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("CredentialRef", body.GetProperty("message").GetString() ?? "");
    }

    [Fact]
    public async Task Get_ExistingConfig_Returns200()
    {
        var id = await CreateAndGetId("MyApi", "https://my.api.io");

        var response = await _client.GetAsync($"/api/server-configs/{id}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("MyApi", body.GetProperty("name").GetString());
        Assert.Equal("https://my.api.io", body.GetProperty("baseUrl").GetString());
    }

    [Fact]
    public async Task Get_UnknownId_Returns404()
    {
        var response = await _client.GetAsync("/api/server-configs/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_AfterTwoCreates_ReturnsBoth()
    {
        await CreateAndGetId("Api A", "https://a.example.com");
        await CreateAndGetId("Api B", "https://b.example.com");

        var response = await _client.GetAsync("/api/server-configs");
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.NotNull(list);
        Assert.True(list!.Length >= 2);
    }

    [Fact]
    public async Task Update_ExistingConfig_ChangesName()
    {
        var id = await CreateAndGetId("Original");

        var putResponse = await _client.PutAsJsonAsync($"/api/server-configs/{id}",
            new { name = "Updated", baseUrl = "https://updated.example.com", securitySchemeType = "none" });
        putResponse.EnsureSuccessStatusCode();

        var getResponse = await _client.GetAsync($"/api/server-configs/{id}");
        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Updated", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Update_UnknownId_Returns404()
    {
        var response = await _client.PutAsJsonAsync("/api/server-configs/does-not-exist",
            new { name = "X", baseUrl = "https://x.com", securitySchemeType = "none" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ExistingConfig_Returns204()
    {
        var id = await CreateAndGetId();

        var deleteResponse = await _client.DeleteAsync($"/api/server-configs/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/api/server-configs/{id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_UnknownId_Returns404()
    {
        var response = await _client.DeleteAsync("/api/server-configs/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithValidCredentialRef_Returns201()
    {
        // Create a credential first
        var credResponse = await _client.PostAsJsonAsync("/api/credentials",
            new { id = "cred-ref-test", name = "Test Cred", value = "secret123" });
        credResponse.EnsureSuccessStatusCode();

        var (response, body) = await Post(new
        {
            name = "Secure Api",
            baseUrl = "https://secure.example.com",
            securitySchemeType = "apiKey",
            credentialRef = "cred-ref-test"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("cred-ref-test", body.GetProperty("credentialRef").GetString());
    }
}
