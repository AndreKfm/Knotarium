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
using Knotarium.Core.Contracts.Options;
using Knotarium.Features.Options;
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
public sealed class OptionsEndpointTests : IClassFixture<KnotariumApiFactory>, IDisposable
{
    private static readonly byte[] TestPrivateKey = Enumerable.Range(1, 32).Select(v => (byte)v).ToArray();
    private static readonly string TestPublicKey = Convert.ToBase64String(PackageSigner.DerivePublicKey(TestPrivateKey));

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _databasePath;
    private readonly string _tempWorkflowStoreFolder;
    private readonly HttpClient _client;

    // A loader that returns a fixed list — exercises the happy-path envelope.
    private sealed class FakeOkLoader : IOptionsLoader
    {
        public string Name => "test.ok";
        public Task<OptionListResult> LoadAsync(OptionLoadContext context, CancellationToken ct) =>
            Task.FromResult(new OptionListResult(new[]
            {
                new OptionItem("Front Office", "res_7f3a"),
                new OptionItem("Warehouse", "res_22b1"),
            }, HasMore: false, NextPage: null));
    }

    // A loader that always fails as if the external system were offline.
    private sealed class FakeOfflineLoader : IOptionsLoader
    {
        public string Name => "test.offline";
        public Task<OptionListResult> LoadAsync(OptionLoadContext context, CancellationToken ct) =>
            throw new OptionsLoadException("Could not reach the resource system: connection refused");
    }

    public OptionsEndpointTests(KnotariumApiFactory factory)
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"knotarium-options-tests-{Guid.NewGuid():N}.db");
        _tempWorkflowStoreFolder = Path.Combine(Path.GetTempPath(), $"knotarium-options-wf-{Guid.NewGuid():N}");

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
                services.AddScoped<Knotarium.Core.Contracts.IExecutionJournalWriter>(_ => new SqliteExecutionJournalWriter(connectionString));

                var fileStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(FileWorkflowStore));
                if (fileStoreDescriptor != null) services.Remove(fileStoreDescriptor);
                var storeDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(Knotarium.Core.Contracts.IWorkflowStore));
                if (storeDescriptor != null) services.Remove(storeDescriptor);
                services.AddScoped(sp => new FileWorkflowStore(_tempWorkflowStoreFolder, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileWorkflowStore>>()));
                services.AddScoped<Knotarium.Core.Contracts.IWorkflowStore>(sp => sp.GetRequiredService<FileWorkflowStore>());

                // Append test loaders to the registry's IEnumerable<IOptionsLoader> source.
                services.AddScoped<IOptionsLoader, FakeOkLoader>();
                services.AddScoped<IOptionsLoader, FakeOfflineLoader>();
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

    private async Task<(HttpResponseMessage Response, JsonElement Body)> LoadOptions(string loaderName, object payload)
    {
        var response = await _client.PostAsJsonAsync($"/api/integrations/test/options/{loaderName}", payload);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response, body);
    }

    [Fact]
    public async Task LoadOptions_HappyPath_Returns200WithOptions()
    {
        var (response, body) = await LoadOptions("test.ok", new { connectionId = "srv1", dependsOn = new { } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = body.GetProperty("options");
        Assert.Equal(2, options.GetArrayLength());
        Assert.Equal("Front Office", options[0].GetProperty("label").GetString());
        Assert.Equal("res_7f3a", options[0].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task LoadOptions_UnknownLoader_Returns404()
    {
        var (response, _) = await LoadOptions("does.not.exist", new { connectionId = "srv1" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LoadOptions_OfflineSystem_Returns200WithErrorEnvelope()
    {
        var (response, body) = await LoadOptions("test.offline", new { connectionId = "srv1" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, body.GetProperty("options").GetArrayLength());
        var error = body.GetProperty("error");
        Assert.Equal("SYSTEM_UNREACHABLE", error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }
}
