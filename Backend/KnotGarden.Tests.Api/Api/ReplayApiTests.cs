using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Execution;
using KnotGarden.Infrastructure.Persistence;
using Xunit;

namespace KnotGarden.Tests.Api;

[Collection(WorkflowExecutionIsolationCollection.Name)]
public sealed class ReplayApiTests : IClassFixture<KnotGardenApiFactory>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _databasePath;

    public ReplayApiTests(KnotGardenApiFactory factory)
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"knotgarden-replay-tests-{Guid.NewGuid():N}.db");
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = _databasePath
        }.ToString();

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

                var writerDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IExecutionJournalWriter));
                if (writerDescriptor != null)
                {
                    services.Remove(writerDescriptor);
                }

                services.AddScoped<IExecutionJournalWriter>(_ => new SqliteExecutionJournalWriter(connectionString));

                // Keep the replay work item inert so the non-idempotent downstream node is never
                // actually executed (no real HTTP). We only assert the endpoint contract here;
                // end-to-end completion is covered by ReplayServiceTests.
                foreach (var hosted in services
                    .Where(d => d.ServiceType == typeof(IHostedService) &&
                                d.ImplementationType == typeof(WorkflowExecutionWorker))
                    .ToList())
                {
                    services.Remove(hosted);
                }
            });
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task<(WorkflowDefinitionId WorkflowId, ExecutionInstanceId SourceId, NodeId HttpNodeId)> SeedCompletedSourceAsync()
    {
        var workflowId = WorkflowDefinitionId.New();
        var sourceId = ExecutionInstanceId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var httpNode = new NodeDefinition(NodeId.Create("http"), "httpRequest", new Dictionary<string, object>
        {
            ["url"] = "https://example.test",
            ["method"] = "GET"
        });
        var endNode = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(
            workflowId,
            "Replay source",
            new[] { startNode, httpNode, endNode },
            new[]
            {
                new EdgeDefinition("e1", startNode.Id, "result", httpNode.Id, "in"),
                new EdgeDefinition("e2", httpNode.Id, "success", endNode.Id, "in")
            });
        var version = new WorkflowVersion(WorkflowVersionId.New(), workflowId, 1, workflow.Nodes, workflow.Edges, DateTimeOffset.UtcNow);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();

        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowVersions.Add(version);
        db.ExecutionInstances.Add(new ExecutionInstance
        {
            Id = sourceId,
            WorkflowDefinitionId = workflowId,
            WorkflowVersionId = version.Id,
            Status = ExecutionStatus.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TriggerOrigin = "manual",
            NodeStates = new List<NodeState>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ExecutionInstanceId = sourceId,
                    NodeId = startNode.Id,
                    Status = NodeStatus.Completed,
                    Outputs = new Dictionary<string, object> { ["result"] = "go" },
                    ExecutionCount = 1,
                    VariablesBefore = "{}"
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    ExecutionInstanceId = sourceId,
                    NodeId = httpNode.Id,
                    Status = NodeStatus.Completed,
                    Outputs = new Dictionary<string, object> { ["success"] = "ok" },
                    ExecutionCount = 1,
                    VariablesBefore = "{}"
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    ExecutionInstanceId = sourceId,
                    NodeId = endNode.Id,
                    Status = NodeStatus.Completed,
                    Outputs = new Dictionary<string, object>(),
                    ExecutionCount = 1,
                    VariablesBefore = "{}"
                }
            }
        });

        await db.SaveChangesAsync();
        return (workflowId, sourceId, httpNode.Id);
    }

    [Fact]
    public async Task PostReplay_FromNonIdempotentNode_Returns202WithWarningAndLineage()
    {
        var client = _factory.CreateClient();
        var (_, sourceId, httpNodeId) = await SeedCompletedSourceAsync();

        var response = await client.PostAsJsonAsync($"/api/executions/{sourceId.Value}/replay", new
        {
            fromNodeId = httpNodeId.Value
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var newExecutionId = body.GetProperty("newExecutionId").GetGuid();
        Assert.NotEqual(Guid.Empty, newExecutionId);

        var warnings = body.GetProperty("warnings").EnumerateArray().ToList();
        var warning = Assert.Single(warnings);
        Assert.Equal("http", warning.GetProperty("nodeId").GetString());
        Assert.Equal(NodeSideEffectKind.NonIdempotentSideEffect.ToString(), warning.GetProperty("sideEffectKind").GetString());

        // A single pending Replay work item was enqueued and lineage was persisted.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var workItem = await db.ExecutionWorkItems.SingleAsync(w => w.ExecutionInstanceId == new ExecutionInstanceId(newExecutionId));
            Assert.Equal("Replay", workItem.Type);

            var replay = await db.ExecutionInstances.SingleAsync(e => e.Id == new ExecutionInstanceId(newExecutionId));
            Assert.Equal("replay", replay.TriggerOrigin);
            Assert.Equal(sourceId, replay.ReplayOfExecutionId);
        }

        // GET lineage chain.
        var lineage = await client.GetFromJsonAsync<JsonElement>($"/api/executions/{sourceId.Value}/replays");
        var lineageEntries = lineage.EnumerateArray().ToList();
        var entry = Assert.Single(lineageEntries);
        Assert.Equal(newExecutionId, entry.GetProperty("id").GetGuid());
        Assert.Equal("http", entry.GetProperty("replayFromNodeId").GetString());
        Assert.Equal(sourceId.Value, entry.GetProperty("replayOfExecutionId").GetGuid());
    }

    [Fact]
    public async Task PostReplay_UnknownSource_Returns404()
    {
        var client = _factory.CreateClient();
        // Ensure schema exists.
        await SeedCompletedSourceAsync();

        var response = await client.PostAsJsonAsync($"/api/executions/{Guid.NewGuid()}/replay", new
        {
            fromNodeId = "anything"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostReplay_UnknownNode_Returns400()
    {
        var client = _factory.CreateClient();
        var (_, sourceId, _) = await SeedCompletedSourceAsync();

        var response = await client.PostAsJsonAsync($"/api/executions/{sourceId.Value}/replay", new
        {
            fromNodeId = "does-not-exist"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
