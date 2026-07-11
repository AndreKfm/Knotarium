using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using KnotGarden.Core.Domain;
using KnotGarden.Infrastructure.Persistence;
using Xunit;

namespace KnotGarden.Tests.Persistence;

public class PersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;

    public PersistenceTests()
    {
        // Setup SQLite In-Memory connection. It must be opened manually and kept open 
        // throughout the test lifecycle so that the in-memory database persists.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information)
            .EnableSensitiveDataLogging()
            .Options;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private async Task<AppDbContext> CreateContextAsync()
    {
        var context = new AppDbContext(_dbContextOptions);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    [Fact]
    public async Task SaveAndRetrieveWorkflow_RestoresStateExactly()
    {
        // Arrange
        using var context = await CreateContextAsync();
        var workflowId = WorkflowDefinitionId.New();
        
        var node1 = new NodeDefinition(
            NodeId.Create("start-1"), 
            "Start", 
            new Dictionary<string, object>
            {
                { "label", "My Start Node" },
                { "x", 100 },
                { "y", 200 }
            }
        );

        var node2 = new NodeDefinition(
            NodeId.Create("end-1"), 
            "End", 
            new Dictionary<string, object>
            {
                { "reason", "Completed successfully" },
                { "flag", true }
            }
        );

        var edge = new EdgeDefinition(
            "edge-1", 
            node1.Id, 
            "success", 
            node2.Id, 
            "in"
        );

        var workflow = new WorkflowDefinition(
            workflowId,
            "Integrations Test Workflow",
            new[] { node1, node2 },
            new[] { edge }
        );

        // Act - Save Workflow
        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        // Act - Retrieve on a clean context to verify serialization
        using var readContext = new AppDbContext(_dbContextOptions);
        var retrieved = await readContext.WorkflowDefinitions.FindAsync(workflowId);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(workflowId, retrieved.Id);
        Assert.Equal("Integrations Test Workflow", retrieved.Name);
        Assert.Equal(2, retrieved.Nodes.Count);
        
        // Assert Node 1 properties
        var retNode1 = retrieved.Nodes.First(n => n.Id.Value == "start-1");
        Assert.Equal("Start", retNode1.Type);
        Assert.Equal("My Start Node", retNode1.Properties["label"].ToString());
        Assert.Equal(100, Convert.ToInt32(retNode1.Properties["x"].ToString()));

        // Assert Node 2 properties
        var retNode2 = retrieved.Nodes.First(n => n.Id.Value == "end-1");
        Assert.Equal("End", retNode2.Type);
        Assert.True(Convert.ToBoolean(retNode2.Properties["flag"].ToString()));

        // Assert Edge
        Assert.Single(retrieved.Edges);
        var retEdge = retrieved.Edges[0];
        Assert.Equal("edge-1", retEdge.Id);
        Assert.Equal(node1.Id, retEdge.From);
        Assert.Equal(node2.Id, retEdge.To);
    }

    [Fact]
    public async Task SqliteWorkflowDefinitionProvider_RetrievesWorkflowCorrectly()
    {
        // Arrange
        using var context = await CreateContextAsync();
        var workflowId = WorkflowDefinitionId.New();
        var workflow = new WorkflowDefinition(
            workflowId,
            "Provider Test Workflow",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>()
        );
        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        // Act
        using var providerContext = new AppDbContext(_dbContextOptions);
        var provider = new SqliteWorkflowDefinitionProvider(providerContext);
        var retrieved = await provider.GetDefinitionAsync(workflowId);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("Provider Test Workflow", retrieved.Name);
    }

    [Fact]
    public async Task ExecutionInstanceCRUD_PerformsLifecycleAndCascadeDeletes()
    {
        // Arrange
        using var context = await CreateContextAsync();
        var executionId = ExecutionInstanceId.New();
        var workflowId = WorkflowDefinitionId.New();

        var execution = new ExecutionInstance
        {
            Id = executionId,
            WorkflowDefinitionId = workflowId,
            Status = ExecutionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            GlobalVariables = new Dictionary<string, object>
            {
                { "test_run", true },
                { "counter", 0 }
            }
        };

        var nodeState = new NodeState
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = executionId,
            NodeId = NodeId.Create("node-1"),
            Status = NodeStatus.Pending,
            Inputs = new Dictionary<string, object> { { "input_val", "foo" } },
            Outputs = new Dictionary<string, object>(),
            ExecutionCount = 0
        };

        var journal = new ExecutionJournal
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = executionId,
            NodeId = NodeId.Create("node-1"),
            Timestamp = DateTimeOffset.UtcNow,
            EventType = "NodeInitialized",
            Message = "Node initialized",
            Data = new Dictionary<string, object> { { "init_prop", "bar" } }
        };

        execution.NodeStates.Add(nodeState);
        execution.JournalEntries.Add(journal);

        // Act - Insert
        context.ExecutionInstances.Add(execution);
        await context.SaveChangesAsync();

        // Act - Retrieve and modify
        using var updateContext = new AppDbContext(_dbContextOptions);
        var retrieved = await updateContext.ExecutionInstances
            .Include(e => e.NodeStates)
            .Include(e => e.JournalEntries)
            .FirstOrDefaultAsync(e => e.Id == executionId);

        Assert.NotNull(retrieved);
        Assert.Equal(ExecutionStatus.Pending, retrieved.Status);
        Assert.Single(retrieved.NodeStates);
        Assert.Single(retrieved.JournalEntries);

        // Update properties
        retrieved.Status = ExecutionStatus.Running;
        retrieved.GlobalVariables["counter"] = 1;
        retrieved.NodeStates[0].Status = NodeStatus.Completed;
        retrieved.NodeStates[0].Outputs["result"] = 42;
        retrieved.NodeStates[0].ExecutionCount = 1;

        var nextJournal = new ExecutionJournal
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = executionId,
            Timestamp = DateTimeOffset.UtcNow,
            EventType = "NodeSuccess",
            Message = "Node completed successfully",
            Data = new Dictionary<string, object> { { "score", 100 } }
        };
        retrieved.JournalEntries.Add(nextJournal);

        await updateContext.SaveChangesAsync();

        // Act - Re-read and Verify Updates
        using var verifyContext = new AppDbContext(_dbContextOptions);
        var final = await verifyContext.ExecutionInstances
            .Include(e => e.NodeStates)
            .Include(e => e.JournalEntries)
            .FirstOrDefaultAsync(e => e.Id == executionId);

        Assert.NotNull(final);
        Assert.Equal(ExecutionStatus.Running, final.Status);
        Assert.Equal(1, Convert.ToInt32(final.GlobalVariables["counter"].ToString()));
        Assert.Equal(NodeStatus.Completed, final.NodeStates[0].Status);
        Assert.Equal(42, Convert.ToInt32(final.NodeStates[0].Outputs["result"].ToString()));
        Assert.Equal(1, final.NodeStates[0].ExecutionCount);
        Assert.Equal(2, final.JournalEntries.Count);

        // Act - Delete (Tests Cascade)
        verifyContext.ExecutionInstances.Remove(final);
        await verifyContext.SaveChangesAsync();

        // Verify Cascade Deletions
        using var emptyContext = new AppDbContext(_dbContextOptions);
        var emptyInstances = await emptyContext.ExecutionInstances.CountAsync();
        var emptyStates = await emptyContext.NodeStates.CountAsync();
        var emptyJournals = await emptyContext.JournalEntries.CountAsync();

        Assert.Equal(0, emptyInstances);
        Assert.Equal(0, emptyStates);
        Assert.Equal(0, emptyJournals);
    }

    [Fact]
    public async Task DatabaseTransactions_EnsuresAtomicRollbacks()
    {
        // Arrange
        using var context = await CreateContextAsync();
        var executionId = ExecutionInstanceId.New();
        var workflowId = WorkflowDefinitionId.New();

        var execution = new ExecutionInstance
        {
            Id = executionId,
            WorkflowDefinitionId = workflowId,
            Status = ExecutionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // Act - Start transaction and save then rollback
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.ExecutionInstances.Add(execution);
            await context.SaveChangesAsync();

            // Rollback explicitly
            await transaction.RollbackAsync();
        }

        // Verify nothing was committed
        using var checkContext = new AppDbContext(_dbContextOptions);
        var found = await checkContext.ExecutionInstances.FirstOrDefaultAsync(e => e.Id == executionId);
        Assert.Null(found);

        // Act - Start transaction and save then commit
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.ExecutionInstances.Add(execution);
            await context.SaveChangesAsync();

            // Commit explicitly
            await transaction.CommitAsync();
        }

        // Verify committed successfully
        using var finalCheck = new AppDbContext(_dbContextOptions);
        var committed = await finalCheck.ExecutionInstances.FirstOrDefaultAsync(e => e.Id == executionId);
        Assert.NotNull(committed);
    }

    [Fact]
    public async Task WorkflowVersionCRUD_PerformsCorrectly()
    {
        using var context = await CreateContextAsync();
        var id = WorkflowVersionId.New();
        var defId = WorkflowDefinitionId.New();

        var version = new WorkflowVersion(
            id,
            defId,
            1,
            new[] { new NodeDefinition(NodeId.Create("start-1"), "Start", new Dictionary<string, object>()) },
            new[] { new EdgeDefinition("edge-1", NodeId.Create("start-1"), "success", NodeId.Create("end-1"), "in") },
            DateTimeOffset.UtcNow
        );

        context.WorkflowVersions.Add(version);
        await context.SaveChangesAsync();

        using var readContext = new AppDbContext(_dbContextOptions);
        var retrieved = await readContext.WorkflowVersions.FindAsync(id);
        Assert.NotNull(retrieved);
        Assert.Equal(defId, retrieved.WorkflowDefinitionId);
        Assert.Equal(1, retrieved.VersionNumber);
        Assert.Single(retrieved.Nodes);
        Assert.Single(retrieved.Edges);
    }

    [Fact]
    public async Task NodePackageCRUD_PerformsCorrectly()
    {
        using var context = await CreateContextAsync();
        var packageId = NodePackageId.Create("http.request");
        var versionId = NodePackageVersionId.New();

        var package = new NodePackage
        {
            Id = packageId,
            DisplayName = "HTTP Request",
            Category = "Network"
        };

        var packageVersion = new NodePackageVersion
        {
            Id = versionId,
            NodePackageId = packageId,
            Version = "1.0.0",
            ManifestJson = "{}",
            Source = "C# code",
            Capabilities = new[] { "http", "credentials" },
            CreatedAt = DateTimeOffset.UtcNow
        };

        package.Versions.Add(packageVersion);
        context.NodePackages.Add(package);
        await context.SaveChangesAsync();

        using var readContext = new AppDbContext(_dbContextOptions);
        var retrieved = await readContext.NodePackages.Include(p => p.Versions).FirstOrDefaultAsync(p => p.Id == packageId);
        Assert.NotNull(retrieved);
        Assert.Equal("HTTP Request", retrieved.DisplayName);
        Assert.Single(retrieved.Versions);
        Assert.Equal(versionId, retrieved.Versions[0].Id);
        Assert.Equal("1.0.0", retrieved.Versions[0].Version);
        Assert.Equal(2, retrieved.Versions[0].Capabilities.Count);
        Assert.Contains("http", retrieved.Versions[0].Capabilities);
    }

    [Fact]
    public async Task CredentialCRUD_PerformsCorrectly()
    {
        using var context = await CreateContextAsync();
        var cred = new Credential
        {
            Id = "secret-1",
            Name = "API_KEY",
            EncryptedValue = "encrypted_secret",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        context.Credentials.Add(cred);
        await context.SaveChangesAsync();

        using var readContext = new AppDbContext(_dbContextOptions);
        var retrieved = await readContext.Credentials.FindAsync("secret-1");
        Assert.NotNull(retrieved);
        Assert.Equal("API_KEY", retrieved.Name);
        Assert.Equal("encrypted_secret", retrieved.EncryptedValue);
    }

    [Fact]
    public async Task AuditEntryCRUD_PerformsCorrectly()
    {
        using var context = await CreateContextAsync();
        var id = Guid.NewGuid();
        var entry = new AuditEntry
        {
            Id = id,
            Action = "Publish",
            Actor = "Admin",
            Timestamp = DateTimeOffset.UtcNow,
            Details = "{}",
            PreviousHash = "prev_hash",
            EntryHash = "entry_hash"
        };

        context.AuditEntries.Add(entry);
        await context.SaveChangesAsync();

        using var readContext = new AppDbContext(_dbContextOptions);
        var retrieved = await readContext.AuditEntries.FindAsync(id);
        Assert.NotNull(retrieved);
        Assert.Equal("Publish", retrieved.Action);
        Assert.Equal("prev_hash", retrieved.PreviousHash);
        Assert.Equal("entry_hash", retrieved.EntryHash);
    }

    [Fact]
    public async Task SqliteExecutionJournalWriter_WritesBypassingEFCore()
    {
        using var context = await CreateContextAsync();
        var executionId = ExecutionInstanceId.New();

        var instance = new ExecutionInstance
        {
            Id = executionId,
            WorkflowDefinitionId = WorkflowDefinitionId.New(),
            Status = ExecutionStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        context.ExecutionInstances.Add(instance);
        await context.SaveChangesAsync();

        // Register the in-memory shared connection writer
        var writer = new SqliteExecutionJournalWriter(_connection.ConnectionString, _connection);

        var entry = new ExecutionJournal
        {
            Id = Guid.NewGuid(),
            ExecutionInstanceId = executionId,
            NodeId = NodeId.Create("node-1"),
            Timestamp = DateTimeOffset.UtcNow,
            EventType = "NodeExecutionStarted",
            Message = "Testing direct ADO.NET writes",
            Data = new Dictionary<string, object> { ["foo"] = "bar" }
        };

        // Write using direct ADO.NET
        await writer.WriteAsync(entry);

        // Read using EF Core to prove it's written and queryable!
        using var readContext = new AppDbContext(_dbContextOptions);
        var retrieved = await readContext.JournalEntries.FirstOrDefaultAsync(j => j.Id == entry.Id);

        Assert.NotNull(retrieved);
        Assert.Equal(executionId, retrieved.ExecutionInstanceId);
        Assert.Equal("node-1", retrieved.NodeId?.Value);
        Assert.Equal("NodeExecutionStarted", retrieved.EventType);
        Assert.Equal("Testing direct ADO.NET writes", retrieved.Message);
        Assert.Equal("bar", retrieved.Data["foo"].ToString());
    }
}
