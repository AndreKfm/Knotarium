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
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Knotarium.Api;
using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Infrastructure.Security;
using Xunit;

namespace Knotarium.Tests.Api;

[Collection(WorkflowExecutionIsolationCollection.Name)]
public class WorkflowApiTests : IClassFixture<KnotariumApiFactory>, IDisposable
{
    private static readonly byte[] TestPrivateKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static readonly string TestPublicKey = Convert.ToBase64String(PackageSigner.DerivePublicKey(TestPrivateKey));

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _databasePath;
    private readonly string _tempWorkflowStoreFolder;
    private readonly string _tempDataDirectory;

    // Enables only the code.execute capability, so the node-editor sandbox tests can compile+run while
    // every other capability stays denied by default.
    private sealed class CodeExecutionEnabledPolicy : Knotarium.Core.Contracts.ICapabilityPolicy
    {
        public Task<bool> IsEnabledAsync(string capability, CancellationToken cancellationToken = default)
            => Task.FromResult(capability == Knotarium.Core.Domain.NodeCapabilities.CodeExecution);
    }

    public WorkflowApiTests(KnotariumApiFactory factory)
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"knotarium-api-tests-{Guid.NewGuid():N}.db");
        _tempWorkflowStoreFolder = Path.Combine(Path.GetTempPath(), $"knotarium-api-wftests-{Guid.NewGuid():N}");
        // Isolate the machine-wide data directory too: unset, the host defaults it to a system path
        // (%ProgramData%\Knotarium / /usr/share/Knotarium) that a CI user can't create or measure, so the
        // disk-space guard reads it as low-on-space and pauses arming — which 409s executions. A writable
        // per-test temp dir keeps the guard happy and stops cross-test/run contamination.
        _tempDataDirectory = Path.Combine(Path.GetTempPath(), $"knotarium-api-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDataDirectory);

        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = _databasePath
        }.ToString();

        // Override DbContext to use an isolated in-memory database for each test run
        _factory = factory.WithWebHostBuilder(builder =>
        {
            // The /api/executions (external-trigger) path is gated on the runtime being armed; seed it
            // armed (matching RuntimeArmingPersistenceTests' UseSetting seam) so the execution tests
            // exercise real behaviour instead of the disarmed 409.
            builder.UseSetting("Runtime:Armed", "true");
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:PackageSigning:TrustedPublicKeys:0"] = TestPublicKey,
                    ["Security:PackageSigning:HostPrivateKeyBase64"] = Convert.ToBase64String(TestPrivateKey),
                    ["Security:Credentials:EncryptionKeyBase64"] = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=",
                    ["Storage:DataDirectory"] = _tempDataDirectory
                });
            });

            builder.ConfigureServices(services =>
            {
                // Remove existing DbContext registration
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseSqlite(connectionString);
                });

                // Override IExecutionJournalWriter to use the in-memory SQLite connection
                var writerDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(IExecutionJournalWriter));
                if (writerDescriptor != null)
                {
                    services.Remove(writerDescriptor);
                }
                services.AddScoped<IExecutionJournalWriter>(_ => new SqliteExecutionJournalWriter(connectionString));

                // Override IWorkflowStore to use a temporary isolated folder for files
                var fileStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(FileWorkflowStore));
                if (fileStoreDescriptor != null)
                {
                    services.Remove(fileStoreDescriptor);
                }
                var storeInterfaceDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IWorkflowStore));
                if (storeInterfaceDescriptor != null)
                {
                    services.Remove(storeInterfaceDescriptor);
                }

                services.AddScoped(sp => new FileWorkflowStore(_tempWorkflowStoreFolder, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileWorkflowStore>>()));
                services.AddScoped<IWorkflowStore>(sp => sp.GetRequiredService<FileWorkflowStore>());

                // The node-editor sandbox-test endpoint compiles+runs real code, gated by the off-by-default
                // code.execute capability. Enable just that one so those tests exercise the compile/run path;
                // other capabilities stay denied (the undeclared-capability test relies on that).
                foreach (var capabilityDescriptor in services
                    .Where(d => d.ServiceType == typeof(Knotarium.Core.Contracts.ICapabilityPolicy)).ToList())
                {
                    services.Remove(capabilityDescriptor);
                }
                services.AddSingleton<Knotarium.Core.Contracts.ICapabilityPolicy>(new CodeExecutionEnabledPolicy());
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

        if (Directory.Exists(_tempWorkflowStoreFolder))
        {
            try
            {
                Directory.Delete(_tempWorkflowStoreFolder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (Directory.Exists(_tempDataDirectory))
        {
            try
            {
                Directory.Delete(_tempDataDirectory, recursive: true);
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
    public async Task GetWorkflows_ReturnsEmptyListSuccessfully()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/workflows");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<WorkflowDefinition>>();
        Assert.NotNull(list);
    }

    [Fact]
    public async Task CreateWorkflow_ValidWorkflow_SavesSuccessfully()
    {
        // Arrange
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var endNode = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());
        var edge = new EdgeDefinition("e1", startNode.Id, "result", endNode.Id, "in");

        var workflow = new WorkflowDefinition(
            workflowId,
            "Valid Integration Workflow",
            new[] { startNode, endNode },
            new[] { edge }
        );

        // Act
        var response = await client.PostAsJsonAsync("/api/workflows", workflow);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<WorkflowDefinition>();
        Assert.NotNull(created);
        Assert.Equal(workflowId.Value, created.Id.Value);
    }


    [Fact]
    public async Task CreateWorkflow_SchedulerNode_PersistsSchedule()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var schedulerNode = new NodeDefinition(
            NodeId.Create("scheduler-1"),
            "scheduler",
            new Dictionary<string, object>
            {
                ["cronExpression"] = "*/5 * * * *",
                ["timezoneId"] = "UTC"
            });
        var logNode = new NodeDefinition(NodeId.Create("log-1"), "log", new Dictionary<string, object> { ["message"] = "scheduled" });
        var workflow = new WorkflowDefinition(
            workflowId,
            "Scheduled Workflow",
            new[] { schedulerNode, logNode },
            new[] { new EdgeDefinition("e1", schedulerNode.Id, "triggeredAt", logNode.Id, "in") });

        var response = await client.PostAsJsonAsync("/api/workflows", workflow);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var schedules = await db.Schedules.Where(schedule => schedule.WorkflowDefinitionId == workflowId).ToListAsync();

        Assert.Single(schedules);
        Assert.Equal("*/5 * * * *", schedules[0].CronExpression);
        Assert.Equal("UTC", schedules[0].TimeZoneId);
        Assert.True(schedules[0].IsActive);
    }

    [Fact]
    public async Task CreateWorkflow_SchedulerNode_WithSecondsCron_PersistsSchedule()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var schedulerNode = new NodeDefinition(
            NodeId.Create("scheduler-seconds-1"),
            "scheduler",
            new Dictionary<string, object>
            {
                ["cronExpression"] = "*/5 * * * * *",
                ["timezoneId"] = "UTC"
            });
        var logNode = new NodeDefinition(NodeId.Create("log-seconds-1"), "log", new Dictionary<string, object> { ["message"] = "scheduled" });
        var workflow = new WorkflowDefinition(
            workflowId,
            "Scheduled Workflow With Seconds",
            new[] { schedulerNode, logNode },
            new[] { new EdgeDefinition("e-seconds-1", schedulerNode.Id, "triggeredAt", logNode.Id, "in") });

        var response = await client.PostAsJsonAsync("/api/workflows", workflow);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var schedules = await db.Schedules.Where(schedule => schedule.WorkflowDefinitionId == workflowId).ToListAsync();

        Assert.Single(schedules);
        Assert.Equal("*/5 * * * * *", schedules[0].CronExpression);
        Assert.Equal("UTC", schedules[0].TimeZoneId);
        Assert.True(schedules[0].IsActive);
    }

    [Fact]
    public async Task GetWorkflowSchedules_ReturnsNextFireForSchedulerNodes()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var schedulerNode = new NodeDefinition(
            NodeId.Create("scheduler-next-fire-1"),
            "scheduler",
            new Dictionary<string, object>
            {
                ["cronExpression"] = "*/5 * * * *",
                ["timezoneId"] = "UTC"
            });
        var logNode = new NodeDefinition(NodeId.Create("log-next-fire-1"), "log", new Dictionary<string, object> { ["message"] = "scheduled" });
        var workflow = new WorkflowDefinition(
            workflowId,
            "Scheduled Workflow Next Fire",
            new[] { schedulerNode, logNode },
            new[] { new EdgeDefinition("e-next-fire-1", schedulerNode.Id, "triggeredAt", logNode.Id, "in") });

        var createResponse = await client.PostAsJsonAsync("/api/workflows", workflow);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var response = await client.GetAsync($"/api/workflows/{workflowId.Value}/schedules");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schedules = json.RootElement.EnumerateArray().ToList();

        Assert.Single(schedules);
        Assert.Equal("scheduler-next-fire-1", schedules[0].GetProperty("nodeId").GetString());
        Assert.Equal("*/5 * * * *", schedules[0].GetProperty("cronExpression").GetString());
        Assert.Equal("UTC", schedules[0].GetProperty("timeZoneId").GetString());
        Assert.True(schedules[0].GetProperty("isActive").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(schedules[0].GetProperty("nextFireAtUtc").GetString()));
    }

    [Fact]
    public async Task GetWorkflowSchedules_WhenPersistedNextFireIsPast_ReturnsFutureDisplayTime()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var schedulerNode = new NodeDefinition(
            NodeId.Create("scheduler-stale-next-fire-1"),
            "scheduler",
            new Dictionary<string, object>
            {
                ["cronExpression"] = "*/5 * * * * *",
                ["timezoneId"] = "UTC"
            });
        var logNode = new NodeDefinition(NodeId.Create("log-stale-next-fire-1"), "log", new Dictionary<string, object> { ["message"] = "scheduled" });
        var workflow = new WorkflowDefinition(
            workflowId,
            "Scheduled Workflow Stale Next Fire",
            new[] { schedulerNode, logNode },
            new[] { new EdgeDefinition("e-stale-next-fire-1", schedulerNode.Id, "triggeredAt", logNode.Id, "in") });

        var createResponse = await client.PostAsJsonAsync("/api/workflows", workflow);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var staleNextFire = DateTimeOffset.UtcNow.AddSeconds(-10);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var schedule = await db.Schedules.SingleAsync(item => item.WorkflowDefinitionId == workflowId);
            schedule.NextFireAtUtc = staleNextFire;
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/api/workflows/{workflowId.Value}/schedules");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schedules = json.RootElement.EnumerateArray().ToList();

        Assert.Single(schedules);
        var displayedNextFire = schedules[0].GetProperty("nextFireAtUtc").GetDateTimeOffset();
        Assert.True(displayedNextFire > DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.True(displayedNextFire > staleNextFire);
    }

    [Fact]
    public async Task FireWorkflowSchedule_CreatesScheduleOriginExecution()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var schedulerNode = new NodeDefinition(
            NodeId.Create("scheduler-fire-1"),
            "scheduler",
            new Dictionary<string, object>
            {
                ["cronExpression"] = "*/5 * * * *",
                ["timezoneId"] = "UTC"
            });
        var logNode = new NodeDefinition(NodeId.Create("log-fire-1"), "log", new Dictionary<string, object> { ["message"] = "scheduled" });
        var workflow = new WorkflowDefinition(
            workflowId,
            "Scheduled Workflow Fire Now",
            new[] { schedulerNode, logNode },
            new[] { new EdgeDefinition("e-fire-1", schedulerNode.Id, "triggeredAt", logNode.Id, "in") });

        var createResponse = await client.PostAsJsonAsync("/api/workflows", workflow);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var versionResponse = await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/versions",
            new SaveVersionRequest(workflow.Nodes, workflow.Edges));
        var version = await versionResponse.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(version);

        var activateResponse = await client.PostAsync($"/api/workflows/{workflowId.Value}/activate/{version.Id.Value}", content: null);
        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);

        var response = await client.PostAsync($"/api/workflows/{workflowId.Value}/schedules/{schedulerNode.Id.Value}/fire", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("schedule", json.RootElement.GetProperty("triggerOrigin").GetString());
        Assert.Equal(workflowId.Value, json.RootElement.GetProperty("workflowDefinitionId").GetString());
    }

    [Fact]
    public async Task FireWorkflowSchedule_WithoutActiveVersion_ReturnsConflict()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var schedulerNode = new NodeDefinition(
            NodeId.Create("scheduler-fire-inactive-1"),
            "scheduler",
            new Dictionary<string, object>
            {
                ["cronExpression"] = "*/5 * * * *",
                ["timezoneId"] = "UTC"
            });
        var logNode = new NodeDefinition(NodeId.Create("log-fire-inactive-1"), "log", new Dictionary<string, object> { ["message"] = "scheduled" });
        var workflow = new WorkflowDefinition(
            workflowId,
            "Scheduled Workflow Fire Inactive",
            new[] { schedulerNode, logNode },
            new[] { new EdgeDefinition("e-fire-inactive-1", schedulerNode.Id, "triggeredAt", logNode.Id, "in") });

        var createResponse = await client.PostAsJsonAsync("/api/workflows", workflow);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var response = await client.PostAsync($"/api/workflows/{workflowId.Value}/schedules/{schedulerNode.Id.Value}/fire", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("no active version", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateWorkflow_CyclicWorkflow_ReturnsBadRequestCompilationError()
    {
        // Arrange
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var node1 = new NodeDefinition(NodeId.Create("node-1"), "start", new Dictionary<string, object>());
        var node2 = new NodeDefinition(NodeId.Create("node-2"), "log", new Dictionary<string, object>());
        var node3 = new NodeDefinition(NodeId.Create("node-3"), "log", new Dictionary<string, object>());
        
        // Cyclic edges downstream from the start node: node-1 -> node-2 -> node-3 -> node-2
        var edge1 = new EdgeDefinition("e1", node1.Id, "result", node2.Id, "in");
        var edge2 = new EdgeDefinition("e2", node2.Id, "result", node3.Id, "in");
        var edge3 = new EdgeDefinition("e3", node3.Id, "result", node2.Id, "in");

        var workflow = new WorkflowDefinition(
            workflowId,
            "Cyclic Test Workflow",
            new[] { node1, node2, node3 },
            new[] { edge1, edge2, edge3 }
        );

        // Act
        var response = await client.PostAsJsonAsync("/api/workflows", workflow);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Workflow failed compilation", json.RootElement.GetProperty("message").GetString());

        var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
        Assert.NotEmpty(diagnostics);
        Assert.Contains(
            diagnostics,
            diagnostic =>
                (diagnostic.TryGetProperty("code", out var codeElement) &&
                 string.Equals(codeElement.GetString(), "ERR_CYCLE_DETECTED", StringComparison.OrdinalIgnoreCase)) ||
                (diagnostic.TryGetProperty("message", out var messageElement) &&
                 messageElement.GetString() is { } message &&
                 message.Contains("cycle", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task GetExecutions_StatusAndSearchFilters_ReturnFilteredProjectionWithTriggerOrigin()
    {
        var client = _factory.CreateClient();

        var retryWorkflow = new WorkflowDefinition(
            WorkflowDefinitionId.New(),
            "Retry Pipeline",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>());

        var completedWorkflow = new WorkflowDefinition(
            WorkflowDefinitionId.New(),
            "Completed Pipeline",
            Array.Empty<NodeDefinition>(),
            Array.Empty<EdgeDefinition>());

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();

            db.WorkflowDefinitions.AddRange(retryWorkflow, completedWorkflow);
            db.ExecutionInstances.AddRange(
                new ExecutionInstance
                {
                    Id = ExecutionInstanceId.New(),
                    WorkflowDefinitionId = retryWorkflow.Id,
                    Status = ExecutionStatus.WaitingForRetry,
                    TriggerOrigin = "schedule",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    GlobalVariables = new Dictionary<string, object>()
                },
                new ExecutionInstance
                {
                    Id = ExecutionInstanceId.New(),
                    WorkflowDefinitionId = completedWorkflow.Id,
                    Status = ExecutionStatus.Completed,
                    TriggerOrigin = "manual",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                    UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                    GlobalVariables = new Dictionary<string, object>()
                });

            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/executions?status=Retrying&search=Retry");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var runs = json.RootElement.EnumerateArray().ToList();

        Assert.Single(runs);
        Assert.Equal("WaitingForRetry", runs[0].GetProperty("status").GetString());
        Assert.Equal("Retry Pipeline", runs[0].GetProperty("workflowName").GetString());
        Assert.Equal("schedule", runs[0].GetProperty("triggerOrigin").GetString());
    }

    [Fact]
    public async Task TriggerWorkflow_RunsDAGExecution_AndStreamsEventsOverSSE()
    {
        // Arrange
        var client = _factory.CreateClient();
        
        // 1. Create a valid workflow
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var logNode = new NodeDefinition(NodeId.Create("log"), "log", new Dictionary<string, object> { ["message"] = "SSE Integration Log" });
        var endNode = new NodeDefinition(NodeId.Create("end"), "end", new Dictionary<string, object>());

        var edge1 = new EdgeDefinition("e1", startNode.Id, "result", logNode.Id, "in");
        var edge2 = new EdgeDefinition("e2", logNode.Id, "result", endNode.Id, "in");

        var workflow = new WorkflowDefinition(
            workflowId,
            "SSE Trigger Integration Workflow",
            new[] { startNode, logNode, endNode },
            new[] { edge1, edge2 }
        );

        var createRes = await client.PostAsJsonAsync("/api/workflows", workflow);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        var versionResponse = await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/versions",
            new SaveVersionRequest(workflow.Nodes, workflow.Edges));
        var version = await versionResponse.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(version);

        var activateResponse = await client.PostAsync($"/api/workflows/{workflowId.Value}/activate/{version.Id.Value}", content: null);
        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);

        // 2. Trigger execution
        var triggerRes = await client.PostAsync($"/api/workflows/{workflowId.Value}/trigger", null);
        Assert.Equal(HttpStatusCode.Accepted, triggerRes.StatusCode);

        var execution = await triggerRes.Content.ReadFromJsonAsync<ExecutionInstance>();
        Assert.NotNull(execution);
        var executionId = execution.Id;

        // 3. Connect to Server-Sent Events stream
        var sseRes = await client.GetAsync($"/api/executions/{executionId}/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, sseRes.StatusCode);
        Assert.Equal("text/event-stream", sseRes.Content.Headers.ContentType?.MediaType);

        using var stream = await sseRes.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // Read first few lines of the stream
        var lines = new List<string>();
        for (int i = 0; i < 15; i++)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break;
            if (!string.IsNullOrEmpty(line))
            {
                lines.Add(line);
            }
        }

        // Assert that the Event-Stream format is correct and includes core traversal milestones
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.StartsWith("event: WorkflowStarted") || l.StartsWith("event: NodeExecutionStarted"));
        Assert.Contains(lines, l => l.Contains("id:"));
        Assert.Contains(lines, l => l.Contains("data:"));
    }

    [Fact]
    public async Task ExecutionEvents_ReplaysEntriesSharingSameTimestampAfterLastEventId()
    {
        var client = _factory.CreateClient();

        var executionId = ExecutionInstanceId.New();
        var workflowId = WorkflowDefinitionId.New();
        var sharedTimestamp = DateTimeOffset.UtcNow;
        var startedEntryId = Guid.NewGuid();
        var completedEntryId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            db.ExecutionInstances.Add(new ExecutionInstance
            {
                Id = executionId,
                WorkflowDefinitionId = workflowId,
                Status = ExecutionStatus.Completed,
                CreatedAt = sharedTimestamp,
                UpdatedAt = sharedTimestamp
            });

            db.JournalEntries.AddRange(
                new ExecutionJournal
                {
                    Id = startedEntryId,
                    ExecutionInstanceId = executionId,
                    NodeId = NodeId.Create("httprequest-1"),
                    Timestamp = sharedTimestamp,
                    EventType = "NodeExecutionStarted",
                    Message = "HTTP node started."
                },
                new ExecutionJournal
                {
                    Id = completedEntryId,
                    ExecutionInstanceId = executionId,
                    NodeId = NodeId.Create("httprequest-1"),
                    Timestamp = sharedTimestamp,
                    EventType = "NodeExecutionCompleted",
                    Message = "HTTP node completed."
                });

            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/executions/{executionId.Value}/events");
        request.Headers.TryAddWithoutValidation("Last-Event-ID", startedEntryId.ToString());

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var lines = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            var line = await reader.ReadLineAsync();
            if (line == null)
            {
                break;
            }

            if (!string.IsNullOrEmpty(line))
            {
                lines.Add(line);
            }

            if (line == string.Empty && lines.Count > 0)
            {
                break;
            }
        }

        Assert.Contains(lines, line => line == $"id: {completedEntryId}");
        Assert.Contains(lines, line => line == "event: NodeExecutionCompleted");
    }

    [Fact]
    public async Task ExecutionEvents_StreamEndsPromptlyOnHostShutdown()
    {
        // Regression guard: an open SSE stream must not hold the host open for the full shutdown timeout.
        // The live tail links IHostApplicationLifetime.ApplicationStopping into its cancellation token, so
        // when the host begins stopping the stream ends at once instead of parking until Kestrel force-
        // aborts the connection (~30s) — which is why the Windows service took so long to stop.
        var client = _factory.CreateClient();

        var executionId = ExecutionInstanceId.New();
        var workflowId = WorkflowDefinitionId.New();
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ExecutionInstances.Add(new ExecutionInstance
            {
                Id = executionId,
                WorkflowDefinitionId = workflowId,
                Status = ExecutionStatus.Running,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.JournalEntries.Add(new ExecutionJournal
            {
                Id = Guid.NewGuid(),
                ExecutionInstanceId = executionId,
                NodeId = NodeId.Create("start"),
                Timestamp = now,
                EventType = "WorkflowStarted",
                Message = "Started."
            });
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync(
            $"/api/executions/{executionId.Value}/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // Consume the catch-up entry so the request is now parked on the live tail — the state that used
        // to block graceful shutdown.
        var firstLine = await reader.ReadLineAsync();
        Assert.False(string.IsNullOrEmpty(firstLine));

        // Begin host shutdown. The linked ApplicationStopping token must end the live tail promptly.
        _factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        // The stream must reach its end quickly — whether as a clean EOF or a cancellation-induced read
        // error, both mean "it stopped waiting". A generous ceiling keeps this non-flaky while still
        // failing the pre-fix hang (which would block here until the delay wins).
        var drained = Task.Run(async () =>
        {
            try { await reader.ReadToEndAsync(); }
            catch { /* a cancelled in-flight response can surface as a read error; the stream still ended */ }
        });
        var finished = await Task.WhenAny(drained, Task.Delay(TimeSpan.FromSeconds(10))) == drained;
        Assert.True(finished, "SSE stream did not end promptly after host shutdown began.");
    }

    [Fact]
    public async Task PutWorkflow_UpdatesSuccessfully()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(workflowId, "Old Name", new[] { startNode }, Array.Empty<EdgeDefinition>());

        var res1 = await client.PostAsJsonAsync("/api/workflows", workflow);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);

        var updated = workflow with { Name = "New Name" };
        var res2 = await client.PutAsJsonAsync($"/api/workflows/{workflowId.Value}", updated);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);

        var res3 = await client.GetAsync($"/api/workflows/{workflowId.Value}");
        var retrieved = await res3.Content.ReadFromJsonAsync<WorkflowDefinition>();
        Assert.NotNull(retrieved);
        Assert.Equal("New Name", retrieved.Name);
    }

    [Fact]
    public async Task DeleteWorkflow_RemovesSuccessfully()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(workflowId, "To Delete", new[] { startNode }, Array.Empty<EdgeDefinition>());

        await client.PostAsJsonAsync("/api/workflows", workflow);

        var res1 = await client.DeleteAsync($"/api/workflows/{workflowId.Value}");
        Assert.Equal(HttpStatusCode.NoContent, res1.StatusCode);

        var res2 = await client.GetAsync($"/api/workflows/{workflowId.Value}");
        Assert.Equal(HttpStatusCode.NotFound, res2.StatusCode);
    }

    [Fact]
    public async Task SaveWorkflowDraftAndPublish_SavesVersionsSuccessfully_AndAutoActivates()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(workflowId, "Draft Workflow", new[] { startNode }, Array.Empty<EdgeDefinition>());

        await client.PostAsJsonAsync("/api/workflows", workflow);

        var draftReq = new SaveVersionRequest(new[] { startNode }, Array.Empty<EdgeDefinition>());
        var draftRes = await client.PostAsJsonAsync($"/api/workflows/{workflowId.Value}/versions", draftReq);
        Assert.Equal(HttpStatusCode.Created, draftRes.StatusCode);
        var version = await draftRes.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(version);
        Assert.Equal(1, version.VersionNumber);

        var publishRes = await client.PostAsJsonAsync($"/api/workflows/{workflowId.Value}/publish", draftReq);
        Assert.Equal(HttpStatusCode.OK, publishRes.StatusCode);

        // Verify that the version was automatically activated upon publish
        var activeVersionResponse = await client.GetAsync($"/api/workflows/{workflowId.Value}/active-version");
        Assert.Equal(HttpStatusCode.OK, activeVersionResponse.StatusCode);

        var activeVersion = await activeVersionResponse.Content.ReadFromJsonAsync<ActiveWorkflowVersion>();
        Assert.NotNull(activeVersion);
        Assert.Equal(workflowId.Value, activeVersion.WorkflowDefinitionId.Value);
        Assert.NotEqual(default, activeVersion.WorkflowVersionId);
    }

    [Fact]
    public async Task Publish_BlocksDeviceBlockWithWiredPinsButNoTarget()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();

        // Device A is wired (event pin) but has no target picked → its rule can't compile.
        var deviceA = new NodeDefinition(NodeId.Create("A"), "externalDevice", new Dictionary<string, object>());
        var deviceB = new NodeDefinition(NodeId.Create("B"), "externalDevice", new Dictionary<string, object> { ["targetId"] = "siteB" });
        var edge = new EdgeDefinition("e1", deviceA.Id, "evt:Motion", deviceB.Id, "act:Record");
        var workflow = new WorkflowDefinition(workflowId, "Device Graph", new[] { deviceA, deviceB }, new[] { edge });

        await client.PostAsJsonAsync("/api/workflows", workflow);

        var req = new SaveVersionRequest(new[] { deviceA, deviceB }, new[] { edge });
        var publishRes = await client.PostAsJsonAsync($"/api/workflows/{workflowId.Value}/publish", req);

        Assert.Equal(HttpStatusCode.BadRequest, publishRes.StatusCode);
        var body = await publishRes.Content.ReadAsStringAsync();
        Assert.Contains("DEVICE_NO_TARGET", body);
    }

    [Fact]
    public async Task GetWorkflowVersions_ReturnsDescendingVersionHistory()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var endNodeV1 = new NodeDefinition(NodeId.Create("end-v1"), "end", new Dictionary<string, object>());
        var endNodeV2 = new NodeDefinition(NodeId.Create("end-v2"), "end", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(
            workflowId,
            "Version History Workflow",
            new[] { startNode, endNodeV1 },
            new[] { new EdgeDefinition("e1", startNode.Id, "result", endNodeV1.Id, "in") });

        await client.PostAsJsonAsync("/api/workflows", workflow);

        await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/versions",
            new SaveVersionRequest(workflow.Nodes, workflow.Edges));

        await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/versions",
            new SaveVersionRequest(
                new[] { startNode, endNodeV2 },
                new[] { new EdgeDefinition("e2", startNode.Id, "result", endNodeV2.Id, "in") }));

        var response = await client.GetAsync($"/api/workflows/{workflowId.Value}/versions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var versions = await response.Content.ReadFromJsonAsync<WorkflowVersionListResponse>();
        Assert.NotNull(versions);
        Assert.Equal(2, versions.TotalCount);
        Assert.Equal(2, versions.Items.Count);
        Assert.Equal(2, versions.Items[0].VersionNumber);
        Assert.Equal(1, versions.Items[1].VersionNumber);
        Assert.Equal(2, versions.Items[0].NodeCount);

        // The detail endpoint serves the full node/edge payload for a single version.
        var latestId = versions.Items[0].Id;
        var detailResponse = await client.GetAsync($"/api/workflows/{workflowId.Value}/versions/{latestId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(detail);
        Assert.Equal(2, detail.VersionNumber);
        Assert.Equal(2, detail.Nodes.Count);
    }

    [Fact]
    public async Task Restore_WithoutActivate_CreatesInactiveForkForwardVersion()
    {
        var client = _factory.CreateClient();
        var (workflowId, v1, v2) = await SeedTwoVersionsAndActivateSecondAsync(client);

        // Restore v1 forward without activating — active pointer must stay on v2.
        var restoreResponse = await client.PostAsync(
            $"/api/workflows/{workflowId.Value}/restore/{v1.Id.Value}", content: null);

        Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);
        using var json = JsonDocument.Parse(await restoreResponse.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("Restored", root.GetProperty("origin").GetString());
        Assert.Equal(3, root.GetProperty("versionNumber").GetInt32());
        Assert.Equal(v1.Id.Value, Guid.Parse(root.GetProperty("restoredFromVersionId").GetString()!));
        Assert.False(root.GetProperty("activated").GetBoolean());

        var activeResponse = await client.GetAsync($"/api/workflows/{workflowId.Value}/active-version");
        var active = await activeResponse.Content.ReadFromJsonAsync<ActiveWorkflowVersion>();
        Assert.NotNull(active);
        Assert.Equal(v2.Id.Value, active.WorkflowVersionId.Value);
    }

    [Fact]
    public async Task Restore_WithActivate_ActivatesForkForwardVersion()
    {
        var client = _factory.CreateClient();
        var (workflowId, v1, _) = await SeedTwoVersionsAndActivateSecondAsync(client);

        var restoreResponse = await client.PostAsync(
            $"/api/workflows/{workflowId.Value}/restore/{v1.Id.Value}?activate=true", content: null);

        Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);
        using var json = JsonDocument.Parse(await restoreResponse.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.True(root.GetProperty("activated").GetBoolean());
        Assert.Equal(3, root.GetProperty("versionNumber").GetInt32());
        var restoredId = Guid.Parse(root.GetProperty("versionId").GetString()!);

        var activeResponse = await client.GetAsync($"/api/workflows/{workflowId.Value}/active-version");
        var active = await activeResponse.Content.ReadFromJsonAsync<ActiveWorkflowVersion>();
        Assert.NotNull(active);
        Assert.Equal(restoredId, active.WorkflowVersionId.Value);
    }

    [Fact]
    public async Task Restore_WithActivate_BringsRestoredContentIntoWorkingDraft()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var logNode = new NodeDefinition(NodeId.Create("log"), "log", new Dictionary<string, object> { ["message"] = "hi" });

        // v1 = a single Start node.
        var workflow = new WorkflowDefinition(workflowId, "Restore Draft Workflow", new[] { startNode }, Array.Empty<EdgeDefinition>());
        await client.PostAsJsonAsync("/api/workflows", workflow);
        var v1Response = await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/versions",
            new SaveVersionRequest(new[] { startNode }, Array.Empty<EdgeDefinition>()));
        var v1 = await v1Response.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(v1);

        // The editor then evolves the working draft to a newer 2-node graph (Start → Log) and saves it.
        var newerDraft = workflow with
        {
            Nodes = new[] { startNode, logNode },
            Edges = new[] { new EdgeDefinition("e1", startNode.Id, "result", logNode.Id, "in") },
        };
        var saveResponse = await client.PutAsJsonAsync($"/api/workflows/{workflowId.Value}", newerDraft);
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);

        // Sanity: the draft the editor loads currently holds the 2-node graph.
        var draftBefore = await client.GetFromJsonAsync<WorkflowDefinition>($"/api/workflows/{workflowId.Value}");
        Assert.NotNull(draftBefore);
        Assert.Equal(2, draftBefore.Nodes.Count);

        // Restore v1 (single node) and activate it.
        var restoreResponse = await client.PostAsync(
            $"/api/workflows/{workflowId.Value}/restore/{v1!.Id.Value}?activate=true", content: null);
        Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);

        // The working draft the editor loads must now reflect the restored, single-node content —
        // not the newer 2-node graph. (Regression: restore used to leave the draft untouched.)
        var draftAfter = await client.GetFromJsonAsync<WorkflowDefinition>($"/api/workflows/{workflowId.Value}");
        Assert.NotNull(draftAfter);
        Assert.Single(draftAfter.Nodes);
        Assert.Equal("start", draftAfter.Nodes[0].Type);
        Assert.Empty(draftAfter.Edges);
    }

    [Fact]
    public async Task ActivatingOlderVersion_RebindsTriggersToThatVersion()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var schedulerNode = new NodeDefinition(
            NodeId.Create("sched-rebind"),
            "scheduler",
            new Dictionary<string, object> { ["cronExpression"] = "*/5 * * * *", ["timezoneId"] = "UTC" });
        var logNode = new NodeDefinition(NodeId.Create("log-rebind"), "log", new Dictionary<string, object> { ["message"] = "hi" });
        var workflow = new WorkflowDefinition(
            workflowId,
            "Trigger Rebind Workflow",
            new[] { schedulerNode, logNode },
            new[] { new EdgeDefinition("e-rebind", schedulerNode.Id, "triggeredAt", logNode.Id, "in") });
        await client.PostAsJsonAsync("/api/workflows", workflow);

        // v1 carries the scheduler node → publishing registers a schedule and activates v1.
        var publishV1 = await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/publish",
            new SaveVersionRequest(workflow.Nodes, workflow.Edges));
        Assert.Equal(HttpStatusCode.OK, publishV1.StatusCode);
        using var v1Json = JsonDocument.Parse(await publishV1.Content.ReadAsStringAsync());
        var v1Id = v1Json.RootElement.GetProperty("version").GetProperty("id").GetString()!;

        // v2 drops the scheduler → publishing it deregisters the schedule and activates v2.
        var startNode = new NodeDefinition(NodeId.Create("start-rebind"), "start", new Dictionary<string, object>());
        var publishV2 = await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/publish",
            new SaveVersionRequest(new[] { startNode }, Array.Empty<EdgeDefinition>()));
        Assert.Equal(HttpStatusCode.OK, publishV2.StatusCode);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var schedules = await db.Schedules.Where(s => s.WorkflowDefinitionId == workflowId).ToListAsync();
            Assert.Empty(schedules);
        }

        // Re-activating v1 must re-bind the trigger to v1's nodes → the schedule comes back.
        var activateV1 = await client.PostAsync($"/api/workflows/{workflowId.Value}/activate/{v1Id}", content: null);
        Assert.Equal(HttpStatusCode.OK, activateV1.StatusCode);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var schedules = await db.Schedules.Where(s => s.WorkflowDefinitionId == workflowId).ToListAsync();
            Assert.Single(schedules);
        }
    }

    [Fact]
    public async Task Export_IsDeterministic_AndImportCreatesInactiveImportedVersion()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(workflowId, "Export Workflow", new[] { startNode }, Array.Empty<EdgeDefinition>());
        await client.PostAsJsonAsync("/api/workflows", workflow);

        var publishResponse = await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/publish",
            new SaveVersionRequest(new[] { startNode }, Array.Empty<EdgeDefinition>()));
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        using var publishJson = JsonDocument.Parse(await publishResponse.Content.ReadAsStringAsync());
        var publishedVersionId = publishJson.RootElement.GetProperty("version").GetProperty("id").GetString();

        // Export twice — the file must be byte-for-byte identical (determinism is the whole point).
        var exportOne = await client.PostAsync($"/api/workflows/{workflowId.Value}/export", content: null);
        Assert.Equal(HttpStatusCode.OK, exportOne.StatusCode);
        using var exportOneJson = JsonDocument.Parse(await exportOne.Content.ReadAsStringAsync());
        var filePath = exportOneJson.RootElement.GetProperty("filePath").GetString()!;
        Assert.True(System.IO.File.Exists(filePath));
        var fileContent = await System.IO.File.ReadAllTextAsync(filePath);

        var exportTwo = await client.PostAsync($"/api/workflows/{workflowId.Value}/export", content: null);
        using var exportTwoJson = JsonDocument.Parse(await exportTwo.Content.ReadAsStringAsync());
        var fileContentAgain = await System.IO.File.ReadAllTextAsync(exportTwoJson.RootElement.GetProperty("filePath").GetString()!);
        Assert.Equal(fileContent, fileContentAgain);

        // Structural sanity: deterministic file carries a manifest (with checksum) and content section.
        Assert.Contains("\"Manifest\"", fileContent, StringComparison.Ordinal);
        Assert.Contains("\"Checksum\"", fileContent, StringComparison.Ordinal);
        Assert.Contains("\"Content\"", fileContent, StringComparison.Ordinal);

        // Import the file → a new immutable Imported version, inactive by default.
        var importContent = new StringContent(fileContent, System.Text.Encoding.UTF8, "application/json");
        var importResponse = await client.PostAsync("/api/workflows/import", importContent);
        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);
        using var importJson = JsonDocument.Parse(await importResponse.Content.ReadAsStringAsync());
        Assert.Equal("Imported", importJson.RootElement.GetProperty("origin").GetString());
        Assert.False(importJson.RootElement.GetProperty("activated").GetBoolean());
        Assert.True(importJson.RootElement.GetProperty("versionNumber").GetInt32() >= 2);

        // Import never activates: the published version stays active.
        var activeResponse = await client.GetAsync($"/api/workflows/{workflowId.Value}/active-version");
        var active = await activeResponse.Content.ReadFromJsonAsync<ActiveWorkflowVersion>();
        Assert.NotNull(active);
        Assert.Equal(publishedVersionId, active.WorkflowVersionId.Value.ToString());
    }

    [Fact]
    public async Task ActivationHistory_RecordsEvents_AndAnswersPointInTime()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(workflowId, "Activation History Workflow", new[] { startNode }, Array.Empty<EdgeDefinition>());
        await client.PostAsJsonAsync("/api/workflows", workflow);

        var request = new SaveVersionRequest(new[] { startNode }, Array.Empty<EdgeDefinition>());

        // Publish auto-activates v1 (event 1).
        var publishResponse = await client.PostAsJsonAsync($"/api/workflows/{workflowId.Value}/publish", request);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);

        // Create and activate v2 (event 2).
        var v2Response = await client.PostAsJsonAsync($"/api/workflows/{workflowId.Value}/versions", request);
        var v2 = await v2Response.Content.ReadFromJsonAsync<WorkflowVersion>();
        await client.PostAsync($"/api/workflows/{workflowId.Value}/activate/{v2!.Id.Value}", content: null);

        var historyResponse = await client.GetAsync($"/api/workflows/{workflowId.Value}/activation-history");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var history = await historyResponse.Content.ReadFromJsonAsync<WorkflowActivationHistoryResponse>();
        Assert.NotNull(history);
        Assert.Equal(2, history.TotalCount);
        Assert.Equal(2, history.Items.Count);

        // Most recent first → v2, whose previous-active pointer is v1.
        Assert.Equal(v2.Id.Value, history.Items[0].WorkflowVersionId);
        Assert.NotNull(history.Items[0].PreviousActiveVersionId);

        // Point-in-time: before any activation there was nothing live.
        var pastResponse = await client.GetAsync(
            $"/api/workflows/{workflowId.Value}/active-version-at?atUtc={Uri.EscapeDataString(DateTimeOffset.UnixEpoch.ToString("O"))}");
        Assert.Equal(HttpStatusCode.NoContent, pastResponse.StatusCode);

        // After the latest activation, v2 was live.
        var afterLatest = history.Items[0].ActivatedAtUtc.AddSeconds(1);
        var atResponse = await client.GetAsync(
            $"/api/workflows/{workflowId.Value}/active-version-at?atUtc={Uri.EscapeDataString(afterLatest.ToString("O"))}");
        Assert.Equal(HttpStatusCode.OK, atResponse.StatusCode);
        using var atJson = JsonDocument.Parse(await atResponse.Content.ReadAsStringAsync());
        Assert.Equal(v2.Id.Value, Guid.Parse(atJson.RootElement.GetProperty("workflowVersionId").GetString()!));
    }

    [Fact]
    public async Task DeletePublishedWorkflow_ArchivesAndRetainsVersionHistory()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(workflowId, "Archive Retention Workflow", new[] { startNode }, Array.Empty<EdgeDefinition>());
        await client.PostAsJsonAsync("/api/workflows", workflow);
        await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/publish",
            new SaveVersionRequest(new[] { startNode }, Array.Empty<EdgeDefinition>()));

        // Deleting a published workflow archives it rather than hard-deleting.
        var deleteResponse = await client.DeleteAsync($"/api/workflows/{workflowId.Value}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        using var deleteJson = JsonDocument.Parse(await deleteResponse.Content.ReadAsStringAsync());
        Assert.True(deleteJson.RootElement.GetProperty("archived").GetBoolean());

        // The editable draft is gone...
        var getResponse = await client.GetAsync($"/api/workflows/{workflowId.Value}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);

        // ...but the immutable version history and activation log are retained and still queryable.
        var versionsResponse = await client.GetAsync($"/api/workflows/{workflowId.Value}/versions");
        Assert.Equal(HttpStatusCode.OK, versionsResponse.StatusCode);
        var versions = await versionsResponse.Content.ReadFromJsonAsync<WorkflowVersionListResponse>();
        Assert.NotNull(versions);
        Assert.True(versions.TotalCount >= 1);

        var historyResponse = await client.GetAsync($"/api/workflows/{workflowId.Value}/activation-history");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var history = await historyResponse.Content.ReadFromJsonAsync<WorkflowActivationHistoryResponse>();
        Assert.NotNull(history);
        Assert.True(history.TotalCount >= 1);
    }

    [Fact]
    public async Task PermanentlyDeleteArchivedWorkflow_PurgesHeaderAndVersionHistory()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(workflowId, "Purge Me", new[] { startNode }, Array.Empty<EdgeDefinition>());
        await client.PostAsJsonAsync("/api/workflows", workflow);
        await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/publish",
            new SaveVersionRequest(new[] { startNode }, Array.Empty<EdgeDefinition>()));

        // Archive (the normal delete), then permanently delete.
        await client.DeleteAsync($"/api/workflows/{workflowId.Value}");
        var purgeResponse = await client.DeleteAsync($"/api/workflows/{workflowId.Value}/permanent");
        Assert.Equal(HttpStatusCode.OK, purgeResponse.StatusCode);
        using var purgeJson = JsonDocument.Parse(await purgeResponse.Content.ReadAsStringAsync());
        Assert.True(purgeJson.RootElement.GetProperty("purged").GetBoolean());

        // It is gone from the archived list, and its version history is purged (empty, not retained).
        var archivedResponse = await client.GetAsync("/api/workflows/archived");
        using var archivedJson = JsonDocument.Parse(await archivedResponse.Content.ReadAsStringAsync());
        Assert.DoesNotContain(
            archivedJson.RootElement.EnumerateArray(),
            e => e.GetProperty("id").GetString() == workflowId.Value);

        var versionsResponse = await client.GetAsync($"/api/workflows/{workflowId.Value}/versions");
        var versions = await versionsResponse.Content.ReadFromJsonAsync<WorkflowVersionListResponse>();
        Assert.NotNull(versions);
        Assert.Equal(0, versions.TotalCount);
    }

    [Fact]
    public async Task PermanentlyDeleteWorkflow_RejectedWhenNotArchived()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(workflowId, "Still Live", new[] { startNode }, Array.Empty<EdgeDefinition>());
        await client.PostAsJsonAsync("/api/workflows", workflow);
        await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/publish",
            new SaveVersionRequest(new[] { startNode }, Array.Empty<EdgeDefinition>()));

        // A live (non-archived) workflow cannot be permanently deleted — it must be archived first.
        var purgeResponse = await client.DeleteAsync($"/api/workflows/{workflowId.Value}/permanent");
        Assert.Equal(HttpStatusCode.Conflict, purgeResponse.StatusCode);

        // The version history is untouched.
        var versionsResponse = await client.GetAsync($"/api/workflows/{workflowId.Value}/versions");
        var versions = await versionsResponse.Content.ReadFromJsonAsync<WorkflowVersionListResponse>();
        Assert.NotNull(versions);
        Assert.True(versions.TotalCount >= 1);
    }

    private static async Task<(WorkflowDefinitionId WorkflowId, WorkflowVersion V1, WorkflowVersion V2)>
        SeedTwoVersionsAndActivateSecondAsync(HttpClient client)
    {
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(workflowId, "Restore Workflow", new[] { startNode }, Array.Empty<EdgeDefinition>());
        await client.PostAsJsonAsync("/api/workflows", workflow);

        var request = new SaveVersionRequest(new[] { startNode }, Array.Empty<EdgeDefinition>());
        var v1Response = await client.PostAsJsonAsync($"/api/workflows/{workflowId.Value}/versions", request);
        var v1 = await v1Response.Content.ReadFromJsonAsync<WorkflowVersion>();
        var v2Response = await client.PostAsJsonAsync($"/api/workflows/{workflowId.Value}/versions", request);
        var v2 = await v2Response.Content.ReadFromJsonAsync<WorkflowVersion>();

        await client.PostAsync($"/api/workflows/{workflowId.Value}/activate/{v2!.Id.Value}", content: null);
        return (workflowId, v1!, v2);
    }

    [Fact]
    public async Task GetActiveWorkflowVersion_ReturnsNoContentUntilVersionActivated()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(workflowId, "Active Version Workflow", new[] { startNode }, Array.Empty<EdgeDefinition>());

        await client.PostAsJsonAsync("/api/workflows", workflow);

        var noContentResponse = await client.GetAsync($"/api/workflows/{workflowId.Value}/active-version");
        Assert.Equal(HttpStatusCode.NoContent, noContentResponse.StatusCode);

        var versionResponse = await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/versions",
            new SaveVersionRequest(workflow.Nodes, workflow.Edges));
        var version = await versionResponse.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(version);

        var activateResponse = await client.PostAsync($"/api/workflows/{workflowId.Value}/activate/{version.Id.Value}", content: null);
        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);

        var activeVersionResponse = await client.GetAsync($"/api/workflows/{workflowId.Value}/active-version");
        Assert.Equal(HttpStatusCode.OK, activeVersionResponse.StatusCode);

        var activeVersion = await activeVersionResponse.Content.ReadFromJsonAsync<ActiveWorkflowVersion>();
        Assert.NotNull(activeVersion);
        Assert.Equal(workflowId.Value, activeVersion.WorkflowDefinitionId.Value);
        Assert.Equal(version.Id, activeVersion.WorkflowVersionId);
    }

    [Fact]
    public async Task ActivateWorkflowVersion_UsesActivatedVersionForExecution()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var endNodeV1 = new NodeDefinition(NodeId.Create("end-v1"), "end", new Dictionary<string, object>());
        var endNodeV2 = new NodeDefinition(NodeId.Create("end-v2"), "end", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(
            workflowId,
            "Versioned Workflow",
            new[] { startNode, endNodeV1 },
            new[] { new EdgeDefinition("e1", startNode.Id, "result", endNodeV1.Id, "in") });

        await client.PostAsJsonAsync("/api/workflows", workflow);

        var versionOneRequest = new SaveVersionRequest(workflow.Nodes, workflow.Edges);
        var versionOneResponse = await client.PostAsJsonAsync($"/api/workflows/{workflowId.Value}/versions", versionOneRequest);
        var versionOne = await versionOneResponse.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(versionOne);

        var activateResponse = await client.PostAsync($"/api/workflows/{workflowId.Value}/activate/{versionOne.Id.Value}", content: null);
        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);

        var versionTwoRequest = new SaveVersionRequest(
            new[] { startNode, endNodeV2 },
            new[] { new EdgeDefinition("e2", startNode.Id, "result", endNodeV2.Id, "in") });
        var versionTwoResponse = await client.PostAsJsonAsync($"/api/workflows/{workflowId.Value}/versions", versionTwoRequest);
        Assert.Equal(HttpStatusCode.Created, versionTwoResponse.StatusCode);

        var executionResponse = await client.PostAsJsonAsync(
            "/api/executions",
            new StartExecutionRequest(workflowId.Value, new Dictionary<string, object>()));
        Assert.Equal(HttpStatusCode.Accepted, executionResponse.StatusCode);

        var execution = await executionResponse.Content.ReadFromJsonAsync<ExecutionInstance>();
        Assert.NotNull(execution);
        Assert.Equal(versionOne.Id, execution.WorkflowVersionId);
    }

    [Fact]
    public async Task StartExecution_TriggersBackgroundExecution()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(workflowId, "Exec Workflow", new[] { startNode }, Array.Empty<EdgeDefinition>());

        await client.PostAsJsonAsync("/api/workflows", workflow);

        var versionResponse = await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/versions",
            new SaveVersionRequest(workflow.Nodes, workflow.Edges));
        var version = await versionResponse.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(version);

        var activateResponse = await client.PostAsync($"/api/workflows/{workflowId.Value}/activate/{version.Id.Value}", content: null);
        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);

        var execReq = new StartExecutionRequest(workflowId.Value, new Dictionary<string, object> { ["foo"] = "bar" });
        var execRes = await client.PostAsJsonAsync("/api/executions", execReq);
        Assert.Equal(HttpStatusCode.Accepted, execRes.StatusCode);
        var instance = await execRes.Content.ReadFromJsonAsync<ExecutionInstance>();
        Assert.NotNull(instance);
        Assert.Equal(workflowId.Value, instance.WorkflowDefinitionId.Value);
        Assert.True(instance.WorkflowVersionId.HasValue);
    }

    [Fact]
    public async Task StartExecution_WithoutActiveVersion_ReturnsConflict()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(workflowId, "Inactive Workflow", new[] { startNode }, Array.Empty<EdgeDefinition>());

        await client.PostAsJsonAsync("/api/workflows", workflow);

        var response = await client.PostAsJsonAsync(
            "/api/executions",
            new StartExecutionRequest(workflowId.Value, new Dictionary<string, object>()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("no active version", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TriggerWorkflow_WithoutActiveVersion_ReturnsConflict()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start-trigger-inactive"), "start", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(workflowId, "Inactive Trigger Workflow", new[] { startNode }, Array.Empty<EdgeDefinition>());

        await client.PostAsJsonAsync("/api/workflows", workflow);

        var response = await client.PostAsync($"/api/workflows/{workflowId.Value}/trigger", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("no active version", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetWorkflowEnabled_Disabled_BlocksWebhookAndScheduleFireButAllowsManualTrigger()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var schedulerNode = new NodeDefinition(
            NodeId.Create("scheduler-disabled-1"),
            "scheduler",
            new Dictionary<string, object>
            {
                ["cronExpression"] = "*/5 * * * *",
                ["timezoneId"] = "UTC"
            });
        var logNode = new NodeDefinition(NodeId.Create("log-disabled-1"), "log", new Dictionary<string, object> { ["message"] = "x" });
        var workflow = new WorkflowDefinition(
            workflowId,
            "Toggle Workflow",
            new[] { schedulerNode, logNode },
            new[] { new EdgeDefinition("e-disabled-1", schedulerNode.Id, "triggeredAt", logNode.Id, "in") });

        await client.PostAsJsonAsync("/api/workflows", workflow);

        var versionResponse = await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/versions",
            new SaveVersionRequest(workflow.Nodes, workflow.Edges));
        var version = await versionResponse.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(version);
        await client.PostAsync($"/api/workflows/{workflowId.Value}/activate/{version.Id.Value}", content: null);

        // Deactivate the workflow.
        var disableResponse = await client.PostAsJsonAsync($"/api/workflows/{workflowId.Value}/enabled", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);

        // Webhook/external trigger is blocked.
        var webhookResponse = await client.PostAsJsonAsync(
            "/api/executions",
            new StartExecutionRequest(workflowId.Value, new Dictionary<string, object>()));
        Assert.Equal(HttpStatusCode.Conflict, webhookResponse.StatusCode);
        Assert.Contains("deactivated", await webhookResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // Manual schedule fire is blocked.
        var fireResponse = await client.PostAsync($"/api/workflows/{workflowId.Value}/schedules/{schedulerNode.Id.Value}/fire", content: null);
        Assert.Equal(HttpStatusCode.Conflict, fireResponse.StatusCode);

        // Manual run still works.
        var triggerResponse = await client.PostAsync($"/api/workflows/{workflowId.Value}/trigger", content: null);
        Assert.Equal(HttpStatusCode.Accepted, triggerResponse.StatusCode);
    }

    [Fact]
    public async Task SetWorkflowEnabled_Disabled_CancelsInFlightExecutionsAndDropsPendingWorkItems()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>());
        var workflow = new WorkflowDefinition(workflowId, "Cancel On Disable", new[] { startNode }, Array.Empty<EdgeDefinition>());

        await client.PostAsJsonAsync("/api/workflows", workflow);

        var runningExecutionId = ExecutionInstanceId.New();
        var pendingWorkItemId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ExecutionInstances.Add(new ExecutionInstance
            {
                Id = runningExecutionId,
                WorkflowDefinitionId = workflowId,
                Status = ExecutionStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                TriggerOrigin = "manual",
                GlobalVariables = new Dictionary<string, object>()
            });
            db.ExecutionWorkItems.Add(new ExecutionWorkItem
            {
                Id = pendingWorkItemId,
                ExecutionInstanceId = runningExecutionId,
                Type = "Resume",
                Payload = "{}",
                Status = WorkItemStatus.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var disableResponse = await client.PostAsJsonAsync($"/api/workflows/{workflowId.Value}/enabled", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var execution = await verificationDb.ExecutionInstances.SingleAsync(item => item.Id == runningExecutionId);
        Assert.Equal(ExecutionStatus.Cancelled, execution.Status);
        Assert.False(await verificationDb.ExecutionWorkItems.AnyAsync(item => item.Id == pendingWorkItemId));
    }

    [Fact]
    public async Task ResumeExecution_WithHeaderToken_ConsumesTokenAndQueuesWorkItem()
    {
        // The resume endpoint enqueues a *Pending* Resume work item; the live WorkflowExecutionWorker
        // then races to flip it to Running, making the Pending assertion below flaky. This test only
        // cares that the endpoint consumed the token, wrote the resume journal entry, and enqueued the
        // work item — not that the worker hasn't picked it up yet. Run against a host with the execution
        // worker removed so the enqueued state stays observable. (Everything else — arming, the isolated
        // DB, capability policy — is inherited from _factory's builder.) Use this factory exclusively so
        // _factory's own host never starts and drains the shared SQLite queue.
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                foreach (var worker in services
                    .Where(d => d.ImplementationType == typeof(Knotarium.Features.Execution.WorkflowExecutionWorker))
                    .ToList())
                {
                    services.Remove(worker);
                }
            });
        });

        var client = factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var executionId = ExecutionInstanceId.New();
        var waitingNodeId = NodeId.Create("wait-webhook");
        string rawToken;
        WorkflowVersionId workflowVersionId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tokenService = scope.ServiceProvider.GetRequiredService<ICorrelationTokenService>();
            var workflow = new WorkflowDefinition(
                workflowId,
                "Resume Workflow",
                new[]
                {
                    new NodeDefinition(NodeId.Create("start"), "start", new Dictionary<string, object>()),
                    new NodeDefinition(waitingNodeId, "delay", new Dictionary<string, object>())
                },
                new[]
                {
                    new EdgeDefinition("e1", NodeId.Create("start"), "result", waitingNodeId, "in")
                });

            var version = new WorkflowVersion(
                WorkflowVersionId.New(),
                workflowId,
                1,
                workflow.Nodes,
                workflow.Edges,
                DateTimeOffset.UtcNow);

            db.WorkflowDefinitions.Add(workflow);
            db.WorkflowVersions.Add(version);
            db.ExecutionInstances.Add(new ExecutionInstance
            {
                Id = executionId,
                WorkflowDefinitionId = workflowId,
                WorkflowVersionId = version.Id,
                Status = ExecutionStatus.Suspended,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            var createdToken = await tokenService.CreateTokenAsync(executionId, waitingNodeId, TimeSpan.FromMinutes(5));
            rawToken = createdToken.RawToken;
            workflowVersionId = version.Id;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/executions/resume")
        {
            Content = JsonContent.Create(new
            {
                payload = new { approval = "ok" }
            })
        };
        request.Headers.TryAddWithoutValidation("X-Knotarium-Token", rawToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var execution = await verificationDb.ExecutionInstances.SingleAsync(item => item.Id == executionId);
        var token = await verificationDb.CorrelationTokens.SingleAsync(item => item.ExecutionInstanceId == executionId);
        var journalEntry = await verificationDb.JournalEntries.SingleAsync(item => item.ExecutionInstanceId == executionId);
        var workItem = await verificationDb.ExecutionWorkItems.SingleAsync(item => item.ExecutionInstanceId == executionId);

        Assert.Equal(ExecutionStatus.Running, execution.Status);
        Assert.NotNull(token.ConsumedAtUtc);
        Assert.Equal(JournalEventTypes.WorkflowResumed, journalEntry.EventType);
        Assert.Equal(waitingNodeId, journalEntry.NodeId);
        Assert.Equal("Resume", workItem.Type);
        Assert.Equal(WorkItemStatus.Pending, workItem.Status);
        Assert.Contains(workflowVersionId.Value.ToString(), workItem.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approval", workItem.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResumeExecution_WithBodyToken_BindsWithoutHeader()
    {
        var client = _factory.CreateClient();
        var workflowId = WorkflowDefinitionId.New();
        var executionId = ExecutionInstanceId.New();
        var waitingNodeId = NodeId.Create("wait-body");
        string rawToken;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tokenService = scope.ServiceProvider.GetRequiredService<ICorrelationTokenService>();
            var workflow = new WorkflowDefinition(
                workflowId,
                "Resume Workflow Body",
                new[] { new NodeDefinition(waitingNodeId, "delay", new Dictionary<string, object>()) },
                Array.Empty<EdgeDefinition>());

            var version = new WorkflowVersion(
                WorkflowVersionId.New(),
                workflowId,
                1,
                workflow.Nodes,
                workflow.Edges,
                DateTimeOffset.UtcNow);

            db.WorkflowDefinitions.Add(workflow);
            db.WorkflowVersions.Add(version);
            db.ExecutionInstances.Add(new ExecutionInstance
            {
                Id = executionId,
                WorkflowDefinitionId = workflowId,
                WorkflowVersionId = version.Id,
                Status = ExecutionStatus.Suspended,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            var createdToken = await tokenService.CreateTokenAsync(executionId, waitingNodeId, TimeSpan.FromMinutes(5));
            rawToken = createdToken.RawToken;
        }

        var response = await client.PostAsJsonAsync("/api/executions/resume", new
        {
            token = rawToken,
            payload = new { source = "body" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verificationScope = _factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var workItem = await verificationDb.ExecutionWorkItems.SingleAsync(item => item.ExecutionInstanceId == executionId);

        Assert.Contains("body", workItem.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Credentials_CRUD_MasksRawValues()
    {
        var client = _factory.CreateClient();

        var credReq = new CreateCredentialRequest("sec-id", "SECRET_KEY", "super_secret_value");
        var res1 = await client.PostAsJsonAsync("/api/credentials", credReq);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);
        var created = await res1.Content.ReadFromJsonAsync<Credential>();
        Assert.NotNull(created);
        Assert.Equal("***", created.EncryptedValue);

        var res2 = await client.GetAsync("/api/credentials");
        var list = await res2.Content.ReadFromJsonAsync<List<Credential>>();
        Assert.NotNull(list);
        var matched = list.FirstOrDefault(c => c.Id == "sec-id");
        Assert.NotNull(matched);
        Assert.Equal("***", matched.EncryptedValue);

        var res3 = await client.DeleteAsync("/api/credentials/sec-id");
        Assert.Equal(HttpStatusCode.NoContent, res3.StatusCode);
    }

    [Fact]
    public async Task GetNodePackages_ListsBuiltInsAndAllowsInstallation()
    {
        var client = _factory.CreateClient();

        var res1 = await client.GetAsync("/api/node-packages");
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        var list = await res1.Content.ReadAsStringAsync();
        Assert.Contains("start", list);
        Assert.Contains("log", list);

        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "package", "test.zip");
        content.Add(new StringContent("invalid"), "signature");
        var res2 = await client.PostAsync("/api/node-packages/install", content);
        Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode);

        var installPayload = new PackageSigningPayload(
            "my-node",
            "1.0.0",
            "My Custom Node",
            "Utility",
            "{}",
            "ZIP Extracted Binary",
            new List<string> { "logging" });

        var validInstallSignature = PackageSigner.Sign(installPayload, TestPrivateKey);

        var content2 = new MultipartFormDataContent();
        content2.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "package", "my-node.zip");
        content2.Add(new StringContent(validInstallSignature), "signature");
        content2.Add(new StringContent("my-node"), "packageId");
        content2.Add(new StringContent("My Custom Node"), "displayName");
        content2.Add(new StringContent("Utility"), "category");
        content2.Add(new StringContent("{}"), "manifestJson");
        var res3 = await client.PostAsync("/api/node-packages/install", content2);
        Assert.Equal(HttpStatusCode.OK, res3.StatusCode);
    }

    [Fact]
    public async Task GetNodePackages_ReturnsUnifiedBuiltInAndCustomRegistry()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.NodePackages.Add(new NodePackage
            {
                Id = new NodePackageId("custom.unified.node"),
                DisplayName = "Custom Unified Node",
                Category = "Utility",
                Versions = new List<NodePackageVersion>
                {
                    new()
                    {
                        Id = NodePackageVersionId.New(),
                        NodePackageId = new NodePackageId("custom.unified.node"),
                        Version = "1.2.3",
                        ManifestJson = "{\"id\":\"custom.unified.node\",\"version\":\"1.2.3\",\"displayName\":\"Custom Unified Node\",\"category\":\"Utility\",\"tier\":\"Declarative\",\"sideEffectKind\":\"IdempotentSideEffect\",\"recoveryMode\":\"FailImmediately\",\"defaultTimeoutSeconds\":10,\"capabilities\":[],\"parameters\":[],\"outputs\":[{\"name\":\"success\"}]}",
                        Source = "Published",
                        Capabilities = Array.Empty<string>(),
                        CreatedAt = DateTimeOffset.UtcNow
                    }
                }
            });

            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/node-packages");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var registry = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, registry.ValueKind);

        var packageIds = registry.EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .Where(id => id is not null)
            .ToList();

        Assert.Contains("start", packageIds);
        Assert.Contains("scheduler", packageIds);
        Assert.Contains("webhookTrigger", packageIds);
        Assert.Contains("custom.unified.node", packageIds);

        var customPackage = registry.EnumerateArray().Single(item => item.GetProperty("id").GetString() == "custom.unified.node");
        var builtInPackage = registry.EnumerateArray().Single(item => item.GetProperty("id").GetString() == "scheduler");

        Assert.Equal("Custom Unified Node", customPackage.GetProperty("displayName").GetString());
        Assert.True(customPackage.GetProperty("versions").GetArrayLength() > 0);
        Assert.Contains("triggeredAt", builtInPackage.GetProperty("versions")[0].GetProperty("manifestJson").GetString());
    }

    [Fact]
    public async Task NodeEditorTest_RecordsUndeclaredCapability_AndFails()
    {
        var client = _factory.CreateClient();

        var request = new
        {
            packageId = "sample-node",
            manifestYaml = """
                id: sample-node
                version: 1.0.0
                capabilities:
                  - logging
                """,
            executorCode = """
                using System.Threading;
                using System.Threading.Tasks;
                using Knotarium.Core.Contracts;
                using Knotarium.Core.Domain;

                public class DemoExecutor : INodeExecutor
                {
                    public async ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext context, CancellationToken cancellationToken)
                    {
                        if (context.Credentials != null)
                        {
                            await context.Credentials.GetSecretAsync("sample-secret", cancellationToken);
                        }

                        return new NodeResult("success", null, NodeExecutionStatus.Succeeded);
                    }
                }
                """,
            testsYaml = """
                cases:
                  - name: http usage
                    inputs: {}
                    expectedOutput: success
                """
        };

        var response = await client.PostAsJsonAsync("/api/node-editor/test", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("success", out var success));
        Assert.False(success.GetBoolean());

        var cases = body.GetProperty("cases");
        Assert.True(cases.GetArrayLength() > 0);
        Assert.Contains("Undeclared capability invocation", cases[0].GetProperty("message").GetString());
    }

        [Fact]
        public async Task NodeEditorTest_DeclarativeManifest_SkipsRoslynCompilation()
        {
                var client = _factory.CreateClient();

                var request = new
                {
                        packageId = "log",
                        manifestYaml = "id: log\nversion: 1.0.0\ndisplayName: Log\ncategory: Utility\ntier: declarative\ncapabilities:\n  - logging\nparameters:\n  - name: message\n    type: string\n    required: true\n    expression: true\noutputs:\n  - name: result\n",
                        executorCode = "Built in declarative compiled",
                        testsYaml = "cases:\n  - name: log success\n    inputs:\n      message: Hello Knotarium\n    expectedOutput: result\n"
                };

                var response = await client.PostAsJsonAsync("/api/node-editor/test", request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(body.GetProperty("success").GetBoolean());

                var logs = body.GetProperty("logs").EnumerateArray().Select(x => x.GetString()).ToList();
                Assert.Contains(logs, entry => entry != null && entry.Contains("Manifest tier is declarative", StringComparison.Ordinal));
                Assert.DoesNotContain(logs, entry => entry != null && entry.Contains("[ROSLYN] Compiling executor draft.", StringComparison.Ordinal));
        }

    [Fact]
    public async Task NodeEditorTest_ScriptWrapper_CompilesAndRunsSuccessfully()
    {
        var client = _factory.CreateClient();

        var request = new
        {
            packageId = "script-test-node",
            manifestYaml = """
                id: script-test-node
                version: 1.0.0
                tier: compiled
                capabilities:
                  - logging
                parameters:
                  - name: greeting
                    type: string
                    required: true
                """,
            executorCode = """
                var greeting = Input.Get<string>("greeting");
                Logger.LogInformation("Greeting value: {Greeting}", greeting);
                return Success(new { result = greeting.ToUpperInvariant() });
                """,
            testsYaml = """
                cases:
                  - name: script success
                    inputs:
                      greeting: hello knotarium
                    expectedOutput: success
                """
        };

        var response = await client.PostAsJsonAsync("/api/node-editor/test", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());

        var cases = body.GetProperty("cases").EnumerateArray().ToList();
        Assert.Single(cases);
        Assert.Equal("script success", cases[0].GetProperty("name").GetString());
        Assert.Equal("pass", cases[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task NodeEditorTest_ScriptWrapperNodeExecution_RunsInWorkflowEngineSuccessfully()
    {
        var client = _factory.CreateClient();

        // 1. Manually insert the custom C# script package into the SQLite database
        var packageId = new NodePackageId("custom-script-pkg");
        var manifestJson = """
            {
                "id": "custom-script-pkg",
                "version": "1.0.0",
                "displayName": "Custom Script Package",
                "category": "Utility",
                "tier": "compiled",
                "capabilities": ["logging"],
                "parameters": [
                    { "name": "greeting", "type": "string", "required": true }
                ],
                "outputs": [
                    { "name": "success" }
                ]
            }
            """;
        var sourceCode = """
            var greeting = Input.Get<string>("greeting");
            Logger.LogInformation("Script executed with greeting: {Greeting}", greeting);
            return Success(new { result = greeting.ToUpperInvariant() });
            """;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pkg = new NodePackage
            {
                Id = packageId,
                DisplayName = "Custom Script Package",
                Category = "Utility"
            };
            pkg.Versions.Add(new NodePackageVersion
            {
                Id = NodePackageVersionId.New(),
                NodePackageId = packageId,
                Version = "1.0.0",
                ManifestJson = manifestJson,
                Source = sourceCode,
                Signature = "mock-signature",
                Capabilities = new List<string> { "logging" },
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.NodePackages.Add(pkg);
            await db.SaveChangesAsync();
        }

        // 2. Create a workflow containing our custom C# script package node
        var workflowId = WorkflowDefinitionId.New();
        var startNode = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var customScriptNode = new NodeDefinition(
            NodeId.Create("script-node-1"), 
            "custom-script-pkg", 
            new Dictionary<string, object> { ["greeting"] = "hello from test" }
        );
        var endNode = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());

        var edge1 = new EdgeDefinition("e1", startNode.Id, "result", customScriptNode.Id, "in");
        var edge2 = new EdgeDefinition("e2", customScriptNode.Id, "success", endNode.Id, "in");

        var workflow = new WorkflowDefinition(
            workflowId,
            "C# Script Workflow Integration",
            new[] { startNode, customScriptNode, endNode },
            new[] { edge1, edge2 }
        );

        var createRes = await client.PostAsJsonAsync("/api/workflows", workflow);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        var versionResponse = await client.PostAsJsonAsync(
            $"/api/workflows/{workflowId.Value}/versions",
            new SaveVersionRequest(workflow.Nodes, workflow.Edges));
        var version = await versionResponse.Content.ReadFromJsonAsync<WorkflowVersion>();
        Assert.NotNull(version);

        var activateResponse = await client.PostAsync($"/api/workflows/{workflowId.Value}/activate/{version.Id.Value}", content: null);
        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);

        // 3. Trigger execution
        var triggerRes = await client.PostAsync($"/api/workflows/{workflowId.Value}/trigger", null);
        Assert.Equal(HttpStatusCode.Accepted, triggerRes.StatusCode);

        var execution = await triggerRes.Content.ReadFromJsonAsync<ExecutionInstance>();
        Assert.NotNull(execution);
        var executionId = execution.Id;

        // 4. Poll database until workflow completes or fails
        ExecutionInstance? finishedInstance = null;
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(150);
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            finishedInstance = await db.ExecutionInstances
                .Include(e => e.NodeStates)
                .FirstOrDefaultAsync(e => e.Id == executionId);

            if (finishedInstance != null && 
                (finishedInstance.Status == ExecutionStatus.Completed || finishedInstance.Status == ExecutionStatus.Failed))
            {
                break;
            }
        }

        Assert.NotNull(finishedInstance);
        Assert.Equal(ExecutionStatus.Completed, finishedInstance.Status);

        // Verify the outputs are populated and converted correctly!
        var scriptState = finishedInstance.NodeStates.FirstOrDefault(ns => ns.NodeId == customScriptNode.Id);
        Assert.NotNull(scriptState);
        Assert.Equal(NodeStatus.Completed, scriptState.Status);
        
        Assert.True(scriptState.Outputs.TryGetValue("result", out var resultVal));
        var resultStr = resultVal is JsonElement element ? element.GetString() : resultVal.ToString();
        Assert.Equal("HELLO FROM TEST", resultStr);
    }

    [Fact]
    public async Task PublishEndpoint_RequiresPassingSandboxTestInCurrentSession()
    {
        var client = _factory.CreateClient();

        var publishWithoutTests = new MultipartFormDataContent();
        publishWithoutTests.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "package", "publish-node.zip");
        publishWithoutTests.Add(new StringContent("publish-node"), "packageId");
        publishWithoutTests.Add(new StringContent("1.0.0"), "version");
        publishWithoutTests.Add(new StringContent("Publish Node"), "displayName");
        publishWithoutTests.Add(new StringContent("Utility"), "category");
        publishWithoutTests.Add(new StringContent("{}"), "manifestJson");

        var publishWithoutTestsPayload = new PackageSigningPayload(
            "publish-node",
            "1.0.0",
            "Publish Node",
            "Utility",
            "{}",
            "ZIP Extracted Binary",
            new List<string> { "logging" });

        var noGatePassResponse = await client.PostAsync("/api/node-packages/publish", publishWithoutTests);
        Assert.Equal(HttpStatusCode.BadRequest, noGatePassResponse.StatusCode);

        var sourceCode = """
            using System.Threading;
            using System.Threading.Tasks;
            using Knotarium.Core.Contracts;
            using Knotarium.Core.Domain;

            public class DemoExecutor : INodeExecutor
            {
                public ValueTask<NodeResult> ExecuteAsync(NodeInput input, INodeContext context, CancellationToken cancellationToken)
                {
                    return new ValueTask<NodeResult>(new NodeResult("success", null, NodeExecutionStatus.Succeeded));
                }
            }
            """;

        var testRequest = new
        {
            packageId = "publish-node",
            manifestYaml = """
                id: publish-node
                version: 1.0.0
                capabilities:
                  - logging
                """,
            executorCode = sourceCode,
            testsYaml = """
                cases:
                  - name: success
                    inputs: {}
                    expectedOutput: success
                """
        };

        var testResponse = await client.PostAsJsonAsync("/api/node-editor/test", testRequest);
        Assert.Equal(HttpStatusCode.OK, testResponse.StatusCode);

        var testBody = await testResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(testBody.GetProperty("success").GetBoolean());

        var publishAfterTests = new MultipartFormDataContent();
        publishAfterTests.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "package", "publish-node.zip");
        publishAfterTests.Add(new StringContent("publish-node"), "packageId");
        publishAfterTests.Add(new StringContent("1.0.0"), "version");
        publishAfterTests.Add(new StringContent("Publish Node"), "displayName");
        publishAfterTests.Add(new StringContent("Utility"), "category");
        publishAfterTests.Add(new StringContent("{}"), "manifestJson");
        publishAfterTests.Add(new StringContent(sourceCode), "sourceCode");

        var publishAfterTestsPayload = new PackageSigningPayload(
            "publish-node",
            "1.0.0",
            "Publish Node",
            "Utility",
            "{}",
            sourceCode,
            new List<string> { "logging" });

        var publishResponse = await client.PostAsync("/api/node-packages/publish", publishAfterTests);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
    }

    [Fact]
    public async Task WorkflowGroups_FullCycle_ReturnsExpectedResults()
    {
        var client = _factory.CreateClient();

        // 1. GET empty groups
        var getResponse1 = await client.GetAsync("/api/workflow-groups");
        Assert.Equal(HttpStatusCode.OK, getResponse1.StatusCode);
        
        var container1 = await getResponse1.Content.ReadFromJsonAsync<GroupContainer>();
        Assert.NotNull(container1);
        Assert.Empty(container1.Groups);

        var etag1 = getResponse1.Headers.ETag?.Tag;
        Assert.NotNull(etag1);

        // 2. PUT without If-Match -> 428 Precondition Required
        var container2 = new GroupContainer(1, new[]
        {
            new GroupDefinition("grp_sales", "Sales Group", "#123456")
        });
        var putNoMatchRes = await client.PutAsJsonAsync("/api/workflow-groups", container2);
        Assert.Equal(HttpStatusCode.PreconditionRequired, putNoMatchRes.StatusCode);

        // 3. PUT with mismatched If-Match -> 412 Precondition Failed
        var requestMismatched = new HttpRequestMessage(HttpMethod.Put, "/api/workflow-groups")
        {
            Content = JsonContent.Create(container2)
        };
        requestMismatched.Headers.TryAddWithoutValidation("If-Match", "\"mismatched-etag\"");
        var putMismatchedRes = await client.SendAsync(requestMismatched);
        Assert.Equal(HttpStatusCode.PreconditionFailed, putMismatchedRes.StatusCode);

        // 4. PUT with matching If-Match -> 200 OK
        var requestMatching = new HttpRequestMessage(HttpMethod.Put, "/api/workflow-groups")
        {
            Content = JsonContent.Create(container2)
        };
        requestMatching.Headers.TryAddWithoutValidation("If-Match", etag1);
        var putMatchingRes = await client.SendAsync(requestMatching);
        Assert.Equal(HttpStatusCode.OK, putMatchingRes.StatusCode);
        
        var etag2 = putMatchingRes.Headers.ETag?.Tag;
        Assert.NotNull(etag2);
        Assert.NotEqual(etag1, etag2);

        // 5. GET groups verification
        var getResponse2 = await client.GetAsync("/api/workflow-groups");
        Assert.Equal(HttpStatusCode.OK, getResponse2.StatusCode);
        
        var container3 = await getResponse2.Content.ReadFromJsonAsync<GroupContainer>();
        Assert.NotNull(container3);
        Assert.Single(container3.Groups);
        Assert.Equal("grp_sales", container3.Groups[0].Id);
        Assert.Equal("Sales Group", container3.Groups[0].Name);

        // 6. DELETE invalid syntax -> 400 BadRequest
        var deleteResInvalid = await client.DeleteAsync("/api/workflow-groups/invalid_id");
        Assert.Equal(HttpStatusCode.BadRequest, deleteResInvalid.StatusCode);

        // 7. DELETE unknown but syntactically valid -> 204 NoContent
        var deleteResUnknown = await client.DeleteAsync("/api/workflow-groups/grp_unknown");
        Assert.Equal(HttpStatusCode.NoContent, deleteResUnknown.StatusCode);

        // 8. DELETE existing -> 204 NoContent remapping workflows
        var deleteResExisting = await client.DeleteAsync("/api/workflow-groups/grp_sales");
        Assert.Equal(HttpStatusCode.NoContent, deleteResExisting.StatusCode);

        // 9. Verify deleted list is empty
        var getResponse3 = await client.GetAsync("/api/workflow-groups");
        var container4 = await getResponse3.Content.ReadFromJsonAsync<GroupContainer>();
        Assert.NotNull(container4);
        Assert.Empty(container4.Groups);
    }
}
