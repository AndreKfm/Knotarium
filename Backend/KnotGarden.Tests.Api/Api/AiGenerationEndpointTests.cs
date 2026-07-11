using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Api.Services.Ai;
using KnotGarden.Features.Ai;
using KnotGarden.Core.Domain;
using KnotGarden.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnotGarden.Tests.Api;

[Collection(WorkflowExecutionIsolationCollection.Name)]
public sealed class AiGenerationEndpointTests : IClassFixture<KnotGardenApiFactory>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _databasePath;
    private readonly string _tempWorkflowStoreFolder;
    private readonly HttpClient _client;

    // Stands in for the real pipeline so the endpoint/worker wiring is exercised without an Anthropic call.
    private sealed class FakeRunner : IAiGenerationRunner
    {
        public Task<AiGenerationRunResult> RunAsync(string intent, CancellationToken cancellationToken = default, WorkflowDefinition? currentWorkflow = null)
        {
            var workflow = new WorkflowDefinition(
                WorkflowDefinitionId.New(), "Generated: " + intent,
                new[] { new NodeDefinition(NodeId.Create("t"), "manualTrigger", new Dictionary<string, object>()) },
                Array.Empty<EdgeDefinition>());
            return Task.FromResult(new AiGenerationRunResult(true, workflow, new[] { "weather-api" }, Array.Empty<string>(), 1));
        }
    }

    public AiGenerationEndpointTests(KnotGardenApiFactory factory)
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"knotgarden-ai-tests-{Guid.NewGuid():N}.db");
        _tempWorkflowStoreFolder = Path.Combine(Path.GetTempPath(), $"knotgarden-ai-wf-{Guid.NewGuid():N}");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var db = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (db != null) services.Remove(db);
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

                var fileStore = services.SingleOrDefault(d => d.ServiceType == typeof(FileWorkflowStore));
                if (fileStore != null) services.Remove(fileStore);
                var store = services.SingleOrDefault(d => d.ServiceType == typeof(KnotGarden.Core.Contracts.IWorkflowStore));
                if (store != null) services.Remove(store);
                services.AddScoped(sp => new FileWorkflowStore(_tempWorkflowStoreFolder, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileWorkflowStore>>()));
                services.AddScoped<KnotGarden.Core.Contracts.IWorkflowStore>(sp => sp.GetRequiredService<FileWorkflowStore>());

                // Override the generation runner so the worker doesn't make a real Anthropic call.
                var runner = services.SingleOrDefault(d => d.ServiceType == typeof(IAiGenerationRunner));
                if (runner != null) services.Remove(runner);
                services.AddScoped<IAiGenerationRunner, FakeRunner>();
            });

            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
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

    [Fact]
    public async Task Generate_ThenPoll_ReachesSucceededWithWorkflow()
    {
        var post = await _client.PostAsJsonAsync("/api/ai/generate", new { intent = "ping a webhook each morning" });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var jobId = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(jobId));

        JsonElement body = default;
        var reachedTerminal = false;
        for (var i = 0; i < 50 && !reachedTerminal; i++)
        {
            var get = await _client.GetAsync($"/api/ai/generate/{jobId}");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            body = await get.Content.ReadFromJsonAsync<JsonElement>();
            var status = body.GetProperty("status").GetString();
            if (status is "Succeeded" or "Failed") reachedTerminal = true;
            else await Task.Delay(100);
        }

        Assert.True(reachedTerminal, "Job never reached a terminal state.");
        Assert.Equal("Succeeded", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Object, body.GetProperty("workflow").ValueKind);
        Assert.Contains("weather-api", body.GetProperty("openSlots").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Generate_EmptyIntent_Returns400()
    {
        var post = await _client.PostAsJsonAsync("/api/ai/generate", new { intent = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Poll_UnknownJob_Returns404()
    {
        var get = await _client.GetAsync("/api/ai/generate/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task AiProviderConfig_SetThenGet_RoundTrips()
    {
        var put = await _client.PutAsJsonAsync("/api/settings/ai-provider", new
        {
            vendor = "openai", model = "gpt-4o", credentialRef = "cred-openai", baseUrl = (string?)null, apiVersion = (string?)null, maxTokens = (int?)null,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = await _client.GetAsync("/api/settings/ai-provider");
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("openai", body.GetProperty("vendor").GetString());
        Assert.Equal("gpt-4o", body.GetProperty("model").GetString());
        Assert.Equal("cred-openai", body.GetProperty("credentialRef").GetString());
        // The available-vendor list is advertised for the UI dropdown.
        Assert.Contains("azure", body.GetProperty("availableVendors").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task AiProviderConfig_UnknownVendor_Returns400()
    {
        var put = await _client.PutAsJsonAsync("/api/settings/ai-provider", new
        {
            vendor = "mistral", model = "m", credentialRef = "c",
        });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task AiProviderConfig_WhenUnset_ReturnsNullsAndVendorList()
    {
        var get = await _client.GetAsync("/api/settings/ai-provider");
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("vendor").ValueKind);
        Assert.Equal(4, body.GetProperty("availableVendors").GetArrayLength());
    }
}
