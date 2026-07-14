using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Knotarium.Core.Domain;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Knotarium.Tests.NodeE2E;

/// <summary>
/// Real-node-through-real-engine e2e for the nodes that touch external resources (HTTP, database,
/// filesystem, scripting, notifications). Each external edge is a self-contained local double — a stubbed
/// HTTP handler, an in-memory SQLite DB, a temp directory, a recording notification dispatcher — so the
/// suite runs deterministically with no network. The node task, capability/file-access guards, compiler,
/// and engine are all production code.
/// </summary>
[Collection(WorkflowExecutionIsolationCollection.Name)]
public class ExternalNodeE2ETests
{
    // --- httpRequest ---

    [Fact]
    public async Task HttpRequest_success_emits_status_body_and_isSuccess()
    {
        using var harness = new NodeE2EHarness();
        harness.WithHttpResponse(HttpStatusCode.OK, "{\"ok\":true}");

        var run = await harness.RunNodeAsync("httpRequest", new Dictionary<string, object>
        {
            ["url"] = "https://example.test/api",
            ["method"] = "GET",
        }, outputPort: "success");

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal(NodeStatus.Completed, run.Node.Status);
        Assert.Equal("200", run.Node.Outputs["statusCode"].ToString());
        Assert.Equal("True", run.Node.Outputs["isSuccess"].ToString());
        Assert.Contains("ok", run.Node.Outputs["body"].ToString());
        Assert.True(run.Ran("end-1"));
    }

    [Fact]
    public async Task HttpRequest_non_2xx_fails_the_node_and_the_run()
    {
        using var harness = new NodeE2EHarness();
        harness.WithHttpResponse(HttpStatusCode.InternalServerError, "boom");

        var run = await harness.RunNodeAsync("httpRequest", new Dictionary<string, object>
        {
            ["url"] = "https://example.test/api",
            ["method"] = "GET",
        }, outputPort: "success");

        Assert.Equal(ExecutionStatus.Failed, run.Status);
        Assert.Equal(NodeStatus.Failed, run.Node.Status);
        Assert.False(run.Ran("end-1"));
    }

    // --- inlineCode (capability-gated: code.execute) ---

    [Fact]
    public async Task InlineCode_runs_the_script_and_returns_its_outputs_when_enabled()
    {
        using var harness = new NodeE2EHarness();
        harness.EnableCapability("code.execute");

        var run = await harness.RunNodeAsync("inlineCode", new Dictionary<string, object>
        {
            ["language"] = "csharp",
            ["code"] = "return Success(new { sum = 2 + 3 });",
        });

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal(NodeStatus.Completed, run.Node.Status);
        var sum = Assert.IsType<JsonElement>(run.Node.Outputs["sum"]);
        Assert.Equal(5, sum.GetInt32());
    }

    [Fact]
    public async Task InlineCode_is_denied_when_the_code_execution_capability_is_off()
    {
        using var harness = new NodeE2EHarness();
        // Capability intentionally not enabled — secure default.

        var run = await harness.RunNodeAsync("inlineCode", new Dictionary<string, object>
        {
            ["code"] = "return Success(new { sum = 1 });",
        });

        Assert.Equal(ExecutionStatus.Failed, run.Status);
        Assert.Equal(NodeStatus.Failed, run.Node.Status);
    }

    // --- dbQuery (capability-gated: database) ---

    [Fact]
    public async Task DbQuery_runs_a_select_against_sqlite_and_returns_rows()
    {
        // Shared-cache in-memory SQLite kept alive by one open connection for the test; the node opens its
        // own connection to the same string and sees the seeded table.
        var connectionString = $"Data Source=file:nodee2e_{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        await using (var cmd = keepAlive.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE t(id INTEGER, name TEXT); INSERT INTO t VALUES (1,'alice'),(2,'bob');";
            await cmd.ExecuteNonQueryAsync();
        }

        using var harness = new NodeE2EHarness();
        harness.EnableCapability("database").WithSecret("conn", connectionString);

        var run = await harness.RunNodeAsync("dbQuery", new Dictionary<string, object>
        {
            ["provider"] = "sqlite",
            ["connectionRef"] = "conn",
            ["query"] = "SELECT id, name FROM t ORDER BY id",
        });

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal(NodeStatus.Completed, run.Node.Status);
        // Persisted outputs round-trip through JSON, so the structured payload comes back as a JsonElement.
        var payload = Assert.IsType<JsonElement>(run.Node.Outputs["result"]);
        Assert.Equal(2, payload.GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public async Task DbQuery_is_denied_when_the_database_capability_is_off()
    {
        using var harness = new NodeE2EHarness();
        harness.WithSecret("conn", "Data Source=:memory:");

        var run = await harness.RunNodeAsync("dbQuery", new Dictionary<string, object>
        {
            ["provider"] = "sqlite",
            ["connectionRef"] = "conn",
            ["query"] = "SELECT 1",
        });

        Assert.Equal(ExecutionStatus.Failed, run.Status);
        Assert.Equal(NodeStatus.Failed, run.Node.Status);
    }

    // --- fileWrite + fileRead (file-access-policy gated) ---

    [Fact]
    public async Task FileWrite_then_FileRead_round_trips_content_through_the_disk()
    {
        using var harness = new NodeE2EHarness();
        var path = Path.Combine(harness.WorkDir, "roundtrip.txt");

        var write = await harness.RunNodeAsync("fileWrite", new Dictionary<string, object>
        {
            ["path"] = path,
            ["content"] = "e2e payload",
        });

        Assert.Equal(ExecutionStatus.Completed, write.Status);
        Assert.Equal(NodeStatus.Completed, write.Node.Status);
        Assert.True(File.Exists(path));

        var read = await harness.RunNodeAsync("fileRead", new Dictionary<string, object>
        {
            ["path"] = path,
            ["encoding"] = "utf8",
        });

        Assert.Equal(ExecutionStatus.Completed, read.Status);
        var payload = Assert.IsType<JsonElement>(read.Node.Outputs["result"]);
        Assert.Equal("e2e payload", payload.GetProperty("content").GetString());
    }

    [Fact]
    public async Task FileRead_outside_the_granted_directory_is_denied()
    {
        using var harness = new NodeE2EHarness();
        // A path the permissive policy does NOT grant (system temp root, not the harness WorkDir).
        var outside = Path.Combine(Path.GetTempPath(), $"knotarium-denied-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outside, "secret");
        try
        {
            var run = await harness.RunNodeAsync("fileRead", new Dictionary<string, object>
            {
                ["path"] = outside,
            });

            // A denied read never succeeds: the run does not complete and the value is not delivered
            // downstream. (The engine's recovery policy schedules a retry, so the status is
            // WaitingForRetry rather than an immediate Failed — either way, not Completed.)
            Assert.NotEqual(ExecutionStatus.Completed, run.Status);
            Assert.NotEqual(NodeStatus.Completed, run.Node.Status);
            Assert.False(run.Ran("end-1"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    // --- sendNotification ---

    [Fact]
    public async Task SendNotification_dispatches_to_the_resolved_channel()
    {
        using var harness = new NodeE2EHarness();
        harness.WithNotificationChannel(new NotificationChannel
        {
            Id = "chan-1",
            Name = "Ops",
            Type = NotificationChannelType.Webhook,
        });

        var run = await harness.RunNodeAsync("sendNotification", new Dictionary<string, object>
        {
            ["channelId"] = "chan-1",
            ["subject"] = "Alert",
            ["message"] = "something happened",
        });

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal(NodeStatus.Completed, run.Node.Status);
        var payload = Assert.IsType<JsonElement>(run.Node.Outputs["result"]);
        Assert.True(payload.GetProperty("sent").GetBoolean());

        Assert.Single(harness.SentNotifications);
        Assert.Equal("something happened", harness.SentNotifications[0].Message.Body);
    }

    // --- resourcePicker ---

    [Fact]
    public async Task ResourcePicker_resolves_the_selected_value_and_fresh_label()
    {
        using var harness = new NodeE2EHarness();
        harness.WithResource("pet_rex", "Rex").WithResource("pet_fluffy", "Fluffy");

        var run = await harness.RunNodeAsync("resourcePicker", new Dictionary<string, object>
        {
            ["serverConfigId"] = "srv1",
            ["path"] = "pets",
            ["labelField"] = "name",
            ["valueField"] = "id",
            ["selection"] = new Dictionary<string, object> { ["value"] = "pet_rex", ["label"] = "cached", ["mode"] = "list" },
        });

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal(NodeStatus.Completed, run.Node.Status);
        Assert.Equal("pet_rex", run.Node.Outputs["value"].ToString());
        Assert.Equal("Rex", run.Node.Outputs["label"].ToString());
    }

    [Fact]
    public async Task SendNotification_fails_when_the_channel_does_not_exist()
    {
        using var harness = new NodeE2EHarness();

        var run = await harness.RunNodeAsync("sendNotification", new Dictionary<string, object>
        {
            ["channelId"] = "missing",
            ["message"] = "hello",
        });

        Assert.Equal(ExecutionStatus.Failed, run.Status);
        Assert.Equal(NodeStatus.Failed, run.Node.Status);
        Assert.Empty(harness.SentNotifications);
    }
}
