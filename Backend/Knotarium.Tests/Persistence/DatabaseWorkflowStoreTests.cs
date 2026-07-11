using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Knotarium.Tests.Persistence;

public sealed class DatabaseWorkflowStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;

    public DatabaseWorkflowStoreTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new AppDbContext(_dbContextOptions);
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task GetAsync_WhenWorkflowExists_ReturnsWorkflow()
    {
        using var seedContext = new AppDbContext(_dbContextOptions);
        var workflowId = WorkflowDefinitionId.New();
        seedContext.WorkflowDefinitions.Add(new WorkflowDefinition(
            workflowId,
            "Provider Test Workflow",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>()));
        await seedContext.SaveChangesAsync();

        using var readContext = new AppDbContext(_dbContextOptions);
        IWorkflowStore store = new DatabaseWorkflowStore(readContext);

        var retrieved = await store.GetAsync(workflowId);

        Assert.NotNull(retrieved);
        Assert.Equal("Provider Test Workflow", retrieved.Name);
    }

    [Fact]
    public async Task ListAsync_WhenWorkflowsExist_ReturnsOrderedWorkflows()
    {
        using var seedContext = new AppDbContext(_dbContextOptions);
        seedContext.WorkflowDefinitions.Add(new WorkflowDefinition(
            WorkflowDefinitionId.New(),
            "Bravo",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>()));
        seedContext.WorkflowDefinitions.Add(new WorkflowDefinition(
            WorkflowDefinitionId.New(),
            "Alpha",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>()));
        await seedContext.SaveChangesAsync();

        using var readContext = new AppDbContext(_dbContextOptions);
        IWorkflowStore store = new DatabaseWorkflowStore(readContext);

        var workflows = await store.ListAsync();

        Assert.Equal(2, workflows.Count);
        Assert.Collection(
            workflows,
            workflow => Assert.Equal("Alpha", workflow.Name),
            workflow => Assert.Equal("Bravo", workflow.Name));
    }

    [Fact]
    public async Task UpsertAsync_WhenWorkflowDoesNotExist_CreatesWorkflow()
    {
        using var writeContext = new AppDbContext(_dbContextOptions);
        IWorkflowStore store = new DatabaseWorkflowStore(writeContext);
        var workflow = new WorkflowDefinition(
            WorkflowDefinitionId.New(),
            "Created",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>());

        var persisted = await store.UpsertAsync(workflow);

        Assert.Equal("Created", persisted.Name);

        using var readContext = new AppDbContext(_dbContextOptions);
        var saved = await readContext.WorkflowDefinitions.SingleAsync(item => item.Id == workflow.Id);
        Assert.Equal("Created", saved.Name);
    }

    [Fact]
    public async Task UpdateAsync_WhenWorkflowExists_UpdatesWorkflow()
    {
        var workflowId = WorkflowDefinitionId.New();

        using (var seedContext = new AppDbContext(_dbContextOptions))
        {
            seedContext.WorkflowDefinitions.Add(new WorkflowDefinition(
                workflowId,
                "Original",
                Array.Empty<NodeDefinition>(),
                Array.Empty<EdgeDefinition>()));
            await seedContext.SaveChangesAsync();
        }

        using var writeContext = new AppDbContext(_dbContextOptions);
        IWorkflowStore store = new DatabaseWorkflowStore(writeContext);

        var updated = await store.UpdateAsync(new WorkflowDefinition(
            workflowId,
            "Updated",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>()));

        Assert.NotNull(updated);
        Assert.Equal("Updated", updated.Name);
    }

    [Fact]
    public async Task UpdateAsync_persists_workflow_metadata_group_across_contexts()
    {
        var workflowId = WorkflowDefinitionId.New();

        using (var seedContext = new AppDbContext(_dbContextOptions))
        {
            seedContext.WorkflowDefinitions.Add(new WorkflowDefinition(
                workflowId, "Grouped", Array.Empty<NodeDefinition>(), Array.Empty<EdgeDefinition>()));
            await seedContext.SaveChangesAsync();
        }

        using (var writeContext = new AppDbContext(_dbContextOptions))
        {
            IWorkflowStore store = new DatabaseWorkflowStore(writeContext);
            var existing = await store.GetAsync(workflowId);
            Assert.NotNull(existing);
            await store.UpdateAsync(existing! with { Metadata = new WorkflowMetadata(Group: "Akn") });
        }

        // Fresh context → reads the column from the database, not a tracked instance.
        using (var readContext = new AppDbContext(_dbContextOptions))
        {
            IWorkflowStore store = new DatabaseWorkflowStore(readContext);
            var reloaded = await store.GetAsync(workflowId);
            Assert.Equal("Akn", reloaded!.Metadata?.Group);
        }
    }

    [Fact]
    public async Task DeleteAsync_WhenWorkflowExists_RemovesWorkflow()
    {
        var workflowId = WorkflowDefinitionId.New();

        using (var seedContext = new AppDbContext(_dbContextOptions))
        {
            seedContext.WorkflowDefinitions.Add(new WorkflowDefinition(
                workflowId,
                "Delete Me",
                Array.Empty<NodeDefinition>(),
                Array.Empty<EdgeDefinition>()));
            await seedContext.SaveChangesAsync();
        }

        using var writeContext = new AppDbContext(_dbContextOptions);
        IWorkflowStore store = new DatabaseWorkflowStore(writeContext);

        var deleted = await store.DeleteAsync(workflowId);

        Assert.True(deleted);

        using var readContext = new AppDbContext(_dbContextOptions);
        var exists = await readContext.WorkflowDefinitions.AnyAsync(item => item.Id == workflowId);
        Assert.False(exists);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}