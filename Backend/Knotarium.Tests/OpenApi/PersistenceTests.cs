using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Knotarium.Core.Contracts.OpenApi;
using Knotarium.Core.Domain.OpenApi;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Persistence.OpenApi;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Knotarium.Tests.OpenApi;

public sealed class PersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AppDbContext _db;
    private readonly OpenApiSpecStore _specStore;
    private readonly ServerConfigStore _configStore;

    public PersistenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(_options);
        _db.Database.EnsureCreated();

        _specStore = new OpenApiSpecStore(_db);
        _configStore = new ServerConfigStore(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // -------------------------------------------------------------------------
    // OpenApiSpecStore
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_NewSpec_CreatesVersionOne()
    {
        var saved = await _specStore.SaveAsync(BuildParsedSpec("api-a"));
        Assert.Equal(1, saved.SpecVersionNumber);
    }

    [Fact]
    public async Task SaveAsync_SameSpecIdAgain_IncrementsVersion()
    {
        var spec = BuildParsedSpec("api-b");
        await _specStore.SaveAsync(spec);
        var second = await _specStore.SaveAsync(spec);
        Assert.Equal(2, second.SpecVersionNumber);
    }

    [Fact]
    public async Task GetLatestAsync_ReturnsHighestVersion()
    {
        var spec = BuildParsedSpec("api-c");
        await _specStore.SaveAsync(spec);
        await _specStore.SaveAsync(spec);
        await _specStore.SaveAsync(spec);

        var result = await _specStore.GetLatestAsync(new OpenApiSpecId("api-c"));

        Assert.NotNull(result);
        Assert.Equal(3, result!.Value.Spec.SpecVersionNumber);
    }

    [Fact]
    public async Task GetLatestAsync_UnknownId_ReturnsNull()
    {
        var result = await _specStore.GetLatestAsync(new OpenApiSpecId("does-not-exist"));
        Assert.Null(result);
    }

    [Fact]
    public async Task GetVersionAsync_KnownVersion_ReturnsThatVersion()
    {
        var spec = BuildParsedSpec("api-d");
        await _specStore.SaveAsync(spec);
        await _specStore.SaveAsync(spec);
        await _specStore.SaveAsync(spec);

        var result = await _specStore.GetVersionAsync(new OpenApiSpecId("api-d"), 2);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Spec.SpecVersionNumber);
    }

    [Fact]
    public async Task GetVersionAsync_UnknownVersion_ReturnsNull()
    {
        await _specStore.SaveAsync(BuildParsedSpec("api-e"));
        var result = await _specStore.GetVersionAsync(new OpenApiSpecId("api-e"), 99);
        Assert.Null(result);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllSpecs_OnlyLatestVersion()
    {
        var specA = BuildParsedSpec("list-a");
        var specB = BuildParsedSpec("list-b");

        await _specStore.SaveAsync(specA);
        await _specStore.SaveAsync(specA);
        await _specStore.SaveAsync(specB);
        await _specStore.SaveAsync(specB);

        var list = await _specStore.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.All(list, s => Assert.Equal(2, s.SpecVersionNumber));
    }

    [Fact]
    public async Task GetVersionsAsync_ReturnsAllVersionsAscending()
    {
        var spec = BuildParsedSpec("api-f");
        await _specStore.SaveAsync(spec);
        await _specStore.SaveAsync(spec);
        await _specStore.SaveAsync(spec);

        var versions = await _specStore.GetVersionsAsync(new OpenApiSpecId("api-f"));

        Assert.Equal(3, versions.Count);
        Assert.Equal(1, versions[0].SpecVersionNumber);
        Assert.Equal(2, versions[1].SpecVersionNumber);
        Assert.Equal(3, versions[2].SpecVersionNumber);
    }

    [Fact]
    public async Task GetOperationAsync_KnownId_ReturnsOperation()
    {
        await _specStore.SaveAsync(BuildParsedSpec("api-g"));
        var op = await _specStore.GetOperationAsync(new OpenApiSpecId("api-g"), "listItems");
        Assert.NotNull(op);
        Assert.Equal("listItems", op!.OperationId);
    }

    [Fact]
    public async Task GetOperationAsync_UnknownId_ReturnsNull()
    {
        await _specStore.SaveAsync(BuildParsedSpec("api-h"));
        var op = await _specStore.GetOperationAsync(new OpenApiSpecId("api-h"), "nonExistent");
        Assert.Null(op);
    }

    // -------------------------------------------------------------------------
    // ServerConfigStore
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ServerConfig_CreateAndGet_RoundTrip()
    {
        var config = BuildConfig("cfg-1");
        await _configStore.CreateAsync(config);

        var result = await _configStore.GetAsync("cfg-1");

        Assert.NotNull(result);
        Assert.Equal(config.Name, result!.Name);
        Assert.Equal(config.BaseUrl, result.BaseUrl);
        Assert.Equal(config.SecuritySchemeType, result.SecuritySchemeType);
        Assert.Equal(config.CredentialRef, result.CredentialRef);
    }

    [Fact]
    public async Task ServerConfig_Update_ChangesFields()
    {
        await _configStore.CreateAsync(BuildConfig("cfg-2"));
        var updated = BuildConfig("cfg-2") with { Name = "Updated Name" };
        await _configStore.UpdateAsync(updated);

        var result = await _configStore.GetAsync("cfg-2");
        Assert.Equal("Updated Name", result!.Name);
    }

    [Fact]
    public async Task ServerConfig_Delete_RemovesEntry()
    {
        await _configStore.CreateAsync(BuildConfig("cfg-3"));
        await _configStore.DeleteAsync("cfg-3");
        var result = await _configStore.GetAsync("cfg-3");
        Assert.Null(result);
    }

    [Fact]
    public async Task ServerConfig_List_ReturnsAllEntries()
    {
        await _configStore.CreateAsync(BuildConfig("cfg-4"));
        await _configStore.CreateAsync(BuildConfig("cfg-5"));
        await _configStore.CreateAsync(BuildConfig("cfg-6"));

        var list = await _configStore.ListAsync();
        Assert.Equal(3, list.Count);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ParsedSpec BuildParsedSpec(string id) => new(
        new ImportedSpec(new OpenApiSpecId(id), "My API", "1.0", "openapi3.0",
            ["https://example.com"], [], DateTimeOffset.UtcNow, 0),
        [new ApiOperation("listItems", "GET", "/items", null, [], [], null, [])],
        [],
        []);

    private static ServerConfigInfo BuildConfig(string id) => new(
        id, $"Config {id}", "https://api.example.com",
        new Dictionary<string, string>(),
        "apiKey", "cred-ref-1",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
