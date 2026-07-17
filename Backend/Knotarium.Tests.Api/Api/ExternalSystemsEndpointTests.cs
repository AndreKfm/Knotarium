// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

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
using Knotarium.Core.Contracts;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Knotarium.Tests.Api;

/// <summary>
/// Exercises the generic external-systems admin endpoints against a fake <see cref="IExternalSignalAdmin"/>.
/// Verifies branding passthrough, CRUD shape, Sync, and — crucially — that secrets never round-trip.
/// </summary>
[Collection(WorkflowExecutionIsolationCollection.Name)]
public sealed class ExternalSystemsEndpointTests : IClassFixture<KnotariumApiFactory>, IDisposable
{
    private static readonly byte[] TestPrivateKey = Enumerable.Range(1, 32).Select(v => (byte)v).ToArray();
    private static readonly string TestPublicKey = Convert.ToBase64String(PackageSigner.DerivePublicKey(TestPrivateKey));

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _databasePath;
    private readonly string _tempWorkflowStoreFolder;
    private readonly HttpClient _client;

    // In-memory admin: one system, editable targets, write-only secrets.
    private sealed class FakeAdmin : IExternalSignalAdmin
    {
        private string _systemName = "Site 1";
        private readonly List<MutableTarget> _targets = new();

        private sealed class MutableTarget
        {
            public required string Id;
            public required string Name;
            public required string Host;
            public int Port;
            public string? User;
            public string? Secret; // never leaves the admin
        }

        public ProviderDescriptor Describe() => new(
            "fake", "Fake Workflow", "site", "box", "camera",
            SupportsSync: true, SupportsTestConnection: true, RequiresCredentials: true);

        public Task<ExternalSystemInfo> GetSystemAsync(CancellationToken ct) =>
            Task.FromResult(new ExternalSystemInfo("sys-1", _systemName, _targets.Select(t => Project(t)).ToList()));

        public Task<ExternalSystemInfo> RenameSystemAsync(string name, CancellationToken ct)
        {
            _systemName = name;
            return GetSystemAsync(ct);
        }

        // Observed-only feed: no diagnostics to reset, so just return the current system.
        public Task<ExternalSystemInfo> ClearDiagnosticsAsync(CancellationToken ct) => GetSystemAsync(ct);

        // Declares no options (see Describe), so every key is unknown and must be rejected.
        public Task<ExternalSystemInfo> SetOptionAsync(string key, bool value, CancellationToken ct) =>
            throw new InvalidOperationException($"Unknown option '{key}'.");

        public Task<ExternalTargetInfo> UpsertTargetAsync(ExternalTargetEdit edit, CancellationToken ct)
        {
            var existing = string.IsNullOrEmpty(edit.Id) ? null : _targets.FirstOrDefault(t => t.Id == edit.Id);
            if (existing == null)
            {
                existing = new MutableTarget { Id = $"t{_targets.Count + 1}", Name = edit.Name, Host = edit.Host };
                _targets.Add(existing);
            }
            existing.Name = edit.Name;
            existing.Host = edit.Host;
            existing.Port = edit.Port;
            existing.User = edit.User;
            if (edit.ClearPassword) existing.Secret = null;
            else if (edit.Password != null) existing.Secret = edit.Password;
            return Task.FromResult(Project(existing));
        }

        public Task DeleteTargetAsync(string targetId, CancellationToken ct)
        {
            var t = _targets.FirstOrDefault(x => x.Id == targetId)
                ?? throw new InvalidOperationException($"Unknown target '{targetId}'.");
            _targets.Remove(t);
            return Task.CompletedTask;
        }

        public Task<ExternalTargetInfo> SyncTargetAsync(string targetId, CancellationToken ct)
        {
            var t = _targets.FirstOrDefault(x => x.Id == targetId)
                ?? throw new InvalidOperationException($"Unknown target '{targetId}'.");
            return Task.FromResult(Project(t, synced: true));
        }

        public Task<TargetStatus> TestConnectionAsync(ExternalTargetEdit candidate, CancellationToken ct) =>
            Task.FromResult(new TargetStatus(candidate.Id ?? "new", TargetConnectivity.Online, DateTimeOffset.UtcNow));

        private static ExternalTargetInfo Project(MutableTarget t, bool synced = false) => new(
            t.Id, t.Name, t.Host, t.Port, t.User,
            HasCredential: !string.IsNullOrEmpty(t.Secret),
            Channels: synced ? new[] { new CatalogChannel("1", "Cam 1", 101) } : Array.Empty<CatalogChannel>(),
            Events: synced ? new[] { new CatalogEntry("E", "Event") } : Array.Empty<CatalogEntry>(),
            Actions: synced ? new[] { new CatalogEntry("A", "Action") } : Array.Empty<CatalogEntry>(),
            Status: new TargetStatus(t.Id, TargetConnectivity.Offline));
    }

    public ExternalSystemsEndpointTests(KnotariumApiFactory factory)
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"knotarium-extsys-tests-{Guid.NewGuid():N}.db");
        _tempWorkflowStoreFolder = Path.Combine(Path.GetTempPath(), $"knotarium-extsys-wf-{Guid.NewGuid():N}");
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

                services.AddSingleton<IExternalSignalAdmin, FakeAdmin>();
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

    [Fact]
    public async Task Descriptor_ReturnsProviderBranding()
    {
        var body = await (await _client.GetAsync("/api/external-systems/descriptor")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Fake Workflow", body.GetProperty("displayName").GetString());
        Assert.True(body.GetProperty("supportsSync").GetBoolean());
    }

    [Fact]
    public async Task UpsertTarget_CreatesTarget_AndNeverEchoesSecret()
    {
        var resp = await _client.PostAsJsonAsync("/api/external-systems/targets", new
        {
            name = "Box A", host = "10.0.0.5", port = 0, user = "admin", password = "s3cr3t"
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();

        Assert.DoesNotContain("s3cr3t", raw); // secret is write-only
        var body = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal("Box A", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("hasCredential").GetBoolean());

        // It shows up in the system listing.
        var sys = await (await _client.GetAsync("/api/external-systems")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, sys.GetProperty("targets").GetArrayLength());
    }

    [Fact]
    public async Task SyncTarget_PullsCatalog()
    {
        var created = await (await _client.PostAsJsonAsync("/api/external-systems/targets", new
        {
            name = "Box B", host = "10.0.0.6", port = 0
        })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var synced = await (await _client.PostAsync($"/api/external-systems/targets/{id}/sync", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, synced.GetProperty("channels").GetArrayLength());
        Assert.Equal(1, synced.GetProperty("events").GetArrayLength());
        Assert.Equal(1, synced.GetProperty("actions").GetArrayLength());
    }

    [Fact]
    public async Task DeleteTarget_RemovesIt()
    {
        var created = await (await _client.PostAsJsonAsync("/api/external-systems/targets", new
        {
            name = "Box C", host = "10.0.0.7", port = 0
        })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var del = await _client.DeleteAsync($"/api/external-systems/targets/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var sys = await (await _client.GetAsync("/api/external-systems")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, sys.GetProperty("targets").GetArrayLength());
    }
}
