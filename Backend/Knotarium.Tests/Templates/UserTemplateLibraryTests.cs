using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Knotarium.Api.Services;
using Knotarium.Features.Execution;
using Knotarium.Features.Templates;
using Knotarium.Features.Portability;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Knotarium.Tests.Templates;

public class UserTemplateLibraryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public UserTemplateLibraryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose() => _connection.Dispose();

    private async Task<AppDbContext> CreateContextAsync()
    {
        var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private sealed class FakeSource(string displayName) : IPublishedWorkflowExportSource
    {
        public Task<PublishedWorkflow?> GetAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default)
        {
            var nodes = new[]
            {
                new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>()),
                new NodeDefinition(NodeId.Create("log-1"), "log", new Dictionary<string, object> { ["message"] = "hi" }),
                new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>()),
            };
            var edges = new[]
            {
                new EdgeDefinition("e1", NodeId.Create("start-1"), "result", NodeId.Create("log-1"), "in"),
                new EdgeDefinition("e2", NodeId.Create("log-1"), "result", NodeId.Create("end-1"), "in"),
            };
            var version = new WorkflowVersion(
                WorkflowVersionId.New(), workflowId, 1, nodes, edges, DateTimeOffset.UnixEpoch);
            return Task.FromResult<PublishedWorkflow?>(new PublishedWorkflow(version, displayName));
        }
    }

    private UserTemplateLibrary Library(AppDbContext context, string displayName = "My Flow")
        => new(context, new TemplateExportService(new FakeSource(displayName), context, TimeProvider.System), TimeProvider.System);

    [Fact]
    public async Task Save_then_list_returns_the_saved_template()
    {
        await using var context = await CreateContextAsync();
        var library = Library(context, "Greeter");

        var saved = await library.SaveAsync(new TemplateExportRequest("wf-1", Name: "Greeter Template", Author: "me"));

        Assert.NotNull(saved);
        var listed = Assert.Single(await library.ListAsync());
        Assert.Equal(saved!.TemplateId, listed.TemplateId);
        Assert.Equal("Greeter Template", listed.Manifest.Name); // listing manifest matches the packed manifest
        Assert.Equal("me", listed.Manifest.Author);
    }

    [Fact]
    public async Task Saving_the_same_workflow_twice_upserts_instead_of_duplicating()
    {
        await using var context = await CreateContextAsync();
        var library = Library(context);

        await library.SaveAsync(new TemplateExportRequest("wf-1", Name: "First", TemplateVersion: "1.0.0"));
        await library.SaveAsync(new TemplateExportRequest("wf-1", Name: "Second", TemplateVersion: "2.0.0"));

        var listed = Assert.Single(await library.ListAsync()); // length stays 1 — same TemplateId replaced
        Assert.Equal("Second", listed.Manifest.Name);
        Assert.Equal("2.0.0", listed.Manifest.TemplateVersion);
    }

    [Fact]
    public async Task Saved_archive_round_trips_and_installs_as_a_fresh_draft()
    {
        await using var context = await CreateContextAsync();
        var library = Library(context, "Installable");
        var saved = await library.SaveAsync(new TemplateExportRequest("wf-1"));

        var bytes = await library.GetArchiveBytesAsync(saved!.TemplateId);
        Assert.NotNull(bytes);

        // Reuse the real install path over the stored bytes.
        var importer = new RecordingImporter();
        var installService = new TemplateInstallService(
            context, importer, new TemplateCompatibilityChecker(TemplateTestFactory.Compiler()), new EmptyWorkflowStore());

        var result = await installService.InstallAsync(bytes!, credentialBindings: null);

        Assert.NotEqual("wf-1", result.WorkflowId); // a fresh id, never clobbering the source
        Assert.Single(importer.Imported);
    }

    [Fact]
    public async Task SaveArchive_stores_an_uploaded_kgtpl_and_upserts_by_id()
    {
        await using var context = await CreateContextAsync();
        var library = Library(context);

        // A packed .kgtpl as if uploaded on the Import tab.
        var doc = TemplateTestFactory.LinearDocument("src-archive", "Uploaded Flow", "hi");
        var bytes = TemplateTestFactory.ArchiveFrom(doc);

        var saved = await library.SaveArchiveAsync(bytes);
        Assert.Equal("Uploaded Flow", saved.Manifest.Name);
        Assert.Single(await library.ListAsync());

        // Saving the same archive again upserts (id is stable) — list stays length 1.
        await library.SaveArchiveAsync(bytes);
        Assert.Single(await library.ListAsync());
    }

    [Fact]
    public async Task SaveArchive_rejects_a_tampered_archive()
    {
        await using var context = await CreateContextAsync();
        var library = Library(context);

        await Assert.ThrowsAnyAsync<Exception>(() => library.SaveArchiveAsync(new byte[] { 1, 2, 3, 4 }));
        Assert.Empty(await library.ListAsync());
    }

    [Fact]
    public async Task Remove_deletes_the_saved_template()
    {
        await using var context = await CreateContextAsync();
        var library = Library(context);
        var saved = await library.SaveAsync(new TemplateExportRequest("wf-1"));

        Assert.True(await library.RemoveAsync(saved!.TemplateId));
        Assert.Empty(await library.ListAsync());
        Assert.False(await library.RemoveAsync(saved.TemplateId)); // already gone
    }

    private sealed class RecordingImporter : ITemplateWorkflowImporter
    {
        public List<WorkflowExportDocument> Imported { get; } = new();

        public Task<int> ImportAsync(WorkflowExportDocument document, CancellationToken cancellationToken = default)
        {
            Imported.Add(document);
            return Task.FromResult(Imported.Count);
        }
    }

    private sealed class EmptyWorkflowStore : Knotarium.Core.Contracts.IWorkflowStore
    {
        public Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowDefinition>>(Array.Empty<WorkflowDefinition>());

        public Task<WorkflowDefinition?> GetAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default) => Task.FromResult<WorkflowDefinition?>(null);
        public Task<WorkflowDefinition> UpsertAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default) => Task.FromResult(workflow);
        public Task<WorkflowDefinition?> UpdateAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default) => Task.FromResult<WorkflowDefinition?>(workflow);
        public Task<bool> DeleteAsync(WorkflowDefinitionId workflowId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
