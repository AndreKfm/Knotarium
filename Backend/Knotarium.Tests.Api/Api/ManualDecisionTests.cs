using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Features.Execution;
using Knotarium.Infrastructure.Persistence;
using Xunit;

namespace Knotarium.Tests.Api;

[Collection(WorkflowExecutionIsolationCollection.Name)]
public sealed class ManualDecisionTests : IClassFixture<KnotariumApiFactory>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _databasePath;

    public ManualDecisionTests(KnotariumApiFactory factory)
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"knotarium-manual-decision-tests-{Guid.NewGuid():N}.db");
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

    [Fact]
    public async Task PostManualDecision_Skip_RecordsDecisionAndCompletesDownstreamWorkflow()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var executionId = ExecutionInstanceId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var manualNode = new NodeDefinition(NodeId.Create("http"), "httpRequest", new Dictionary<string, object>
        {
            ["url"] = "https://example.test",
            ["method"] = "GET"
        });
        var endNode = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(
            workflowId,
            "Manual decision skip",
            new[] { startNode, manualNode, endNode },
            new[]
            {
                new EdgeDefinition("e1", startNode.Id, "result", manualNode.Id, "in"),
                new EdgeDefinition("e2", manualNode.Id, "success", endNode.Id, "in")
            });
        var version = new WorkflowVersion(WorkflowVersionId.New(), workflowId, 1, workflow.Nodes, workflow.Edges, DateTimeOffset.UtcNow);
        var attemptId = Guid.NewGuid().ToString();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();

            db.WorkflowDefinitions.Add(workflow);
            db.WorkflowVersions.Add(version);
            db.ExecutionInstances.Add(new ExecutionInstance
            {
                Id = executionId,
                WorkflowDefinitionId = workflowId,
                WorkflowVersionId = version.Id,
                Status = ExecutionStatus.Suspended,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                NodeStates = new List<NodeState>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ExecutionInstanceId = executionId,
                        NodeId = startNode.Id,
                        Status = NodeStatus.Completed,
                        Outputs = new Dictionary<string, object> { ["result"] = true },
                        ExecutionCount = 1
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ExecutionInstanceId = executionId,
                        NodeId = manualNode.Id,
                        Status = NodeStatus.RequiresManualDecision,
                        ErrorMessage = "Manual decision required.",
                        ExecutionCount = 1
                    }
                },
                JournalEntries = new List<ExecutionJournal>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ExecutionInstanceId = executionId,
                        NodeId = manualNode.Id,
                        Timestamp = DateTimeOffset.UtcNow,
                        EventType = JournalEventTypes.AttemptingExternalEffect,
                        Message = "Attempting external effect.",
                        Data = new Dictionary<string, object>
                        {
                            ["NodeId"] = manualNode.Id.Value,
                            ["AttemptId"] = attemptId,
                            ["SideEffectKind"] = NodeSideEffectKind.NonIdempotentSideEffect.ToString()
                        }
                    }
                }
            });

            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync($"/api/executions/{executionId.Value}/nodes/{manualNode.Id.Value}/manual-decision", new
        {
            decision = "Skip",
            reason = "Operator skipped the node.",
            expectedAttemptId = attemptId
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Guid workItemId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var workItem = await db.ExecutionWorkItems.SingleAsync(item => item.ExecutionInstanceId == executionId);
            Assert.Equal("ManualDecision", workItem.Type);
            Assert.Equal(WorkItemStatus.Pending, workItem.Status);
            workItemId = workItem.Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<WorkflowExecutor>();
            await executor.ProcessWorkItemAsync(workItemId);
        }

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var execution = await verificationDb.ExecutionInstances
            .Include(item => item.NodeStates)
            .Include(item => item.JournalEntries)
            .SingleAsync(item => item.Id == executionId);

        Assert.Equal(ExecutionStatus.Completed, execution.Status);
        Assert.Equal(NodeStatus.Completed, execution.NodeStates.Single(item => item.NodeId == manualNode.Id).Status);
        Assert.Equal(NodeStatus.Completed, execution.NodeStates.Single(item => item.NodeId == endNode.Id).Status);
        Assert.Contains(execution.JournalEntries, entry => entry.EventType == JournalEventTypes.ManualDecisionRecorded);
        Assert.Contains(execution.JournalEntries, entry => entry.NodeId == manualNode.Id && entry.EventType == JournalEventTypes.NodeExecutionCompleted);
    }

    [Fact]
    public async Task PostManualDecision_StaleAttemptId_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var executionId = ExecutionInstanceId.New();
        var manualNodeId = NodeId.Create("http");
        var workflow = new WorkflowDefinition(
            workflowId,
            "Manual decision stale attempt",
            new[] { new NodeDefinition(manualNodeId, "httpRequest", new Dictionary<string, object>()) },
            Array.Empty<EdgeDefinition>());
        var version = new WorkflowVersion(WorkflowVersionId.New(), workflowId, 1, workflow.Nodes, workflow.Edges, DateTimeOffset.UtcNow);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();

            db.WorkflowDefinitions.Add(workflow);
            db.WorkflowVersions.Add(version);
            db.ExecutionInstances.Add(new ExecutionInstance
            {
                Id = executionId,
                WorkflowDefinitionId = workflowId,
                WorkflowVersionId = version.Id,
                Status = ExecutionStatus.Suspended,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                NodeStates = new List<NodeState>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ExecutionInstanceId = executionId,
                        NodeId = manualNodeId,
                        Status = NodeStatus.RequiresManualDecision,
                        ExecutionCount = 1
                    }
                },
                JournalEntries = new List<ExecutionJournal>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        ExecutionInstanceId = executionId,
                        NodeId = manualNodeId,
                        Timestamp = DateTimeOffset.UtcNow,
                        EventType = JournalEventTypes.AttemptingExternalEffect,
                        Message = "Attempting external effect.",
                        Data = new Dictionary<string, object>
                        {
                            ["NodeId"] = manualNodeId.Value,
                            ["AttemptId"] = Guid.NewGuid().ToString()
                        }
                    }
                }
            });

            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync($"/api/executions/{executionId.Value}/nodes/{manualNodeId.Value}/manual-decision", new
        {
            decision = "Skip",
            reason = "Stale attempt.",
            expectedAttemptId = Guid.NewGuid().ToString()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await verificationDb.ExecutionWorkItems.Where(item => item.ExecutionInstanceId == executionId).ToListAsync());
    }
}