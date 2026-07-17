// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

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

    // --- aiPrompt ---

    [Fact]
    public async Task AiPrompt_text_mode_emits_the_model_reply_on_result()
    {
        using var harness = new NodeE2EHarness();
        harness.WithChatReply("Bonjour");

        var run = await harness.RunNodeAsync("aiPrompt", new Dictionary<string, object>
        {
            ["prompt"] = "Translate 'Hello' to French.",
        });

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal(NodeStatus.Completed, run.Node.Status);
        Assert.Equal("Bonjour", run.Node.Outputs["result"].ToString());
        Assert.True(run.Ran("end-1"));
        var request = Assert.Single(harness.ChatRequests);
        Assert.Equal("Translate 'Hello' to French.", request.UserMessage);
    }

    [Fact]
    public async Task AiPrompt_json_mode_emits_the_parsed_object_on_result()
    {
        using var harness = new NodeE2EHarness();
        harness.WithChatReply("""{ "sentiment": "positive", "confidence": 0.9 }""");

        var run = await harness.RunNodeAsync("aiPrompt", new Dictionary<string, object>
        {
            ["prompt"] = "Classify the sentiment.",
            ["jsonSchema"] = """{ "type": "object", "properties": { "sentiment": { "type": "string" } } }""",
        });

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal(NodeStatus.Completed, run.Node.Status);
        var result = JsonSerializer.SerializeToElement(run.Node.Outputs["result"]);
        Assert.Equal("positive", result.GetProperty("sentiment").GetString());
    }

    // --- aiRouter ---

    [Fact]
    public async Task AiRouter_routes_only_the_matched_category_branch()
    {
        using var harness = new NodeE2EHarness();
        harness.WithChatReply("Spam");

        var classify = new NodeDefinition(NodeId.Create("classify-1"), "aiRouter", new Dictionary<string, object>
        {
            ["input"] = "Cheap watches, buy now!!!",
            ["categories"] = "Billing, Spam",
        });
        var start = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var logBilling = new NodeDefinition(NodeId.Create("log-billing"), "log", new Dictionary<string, object> { ["message"] = "billing" });
        var logSpam = new NodeDefinition(NodeId.Create("log-spam"), "log", new Dictionary<string, object> { ["message"] = "spam" });
        var logOtherwise = new NodeDefinition(NodeId.Create("log-otherwise"), "log", new Dictionary<string, object> { ["message"] = "otherwise" });
        var end = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());

        var edges = new[]
        {
            new EdgeDefinition("e-start", start.Id, "result", classify.Id, "in"),
            new EdgeDefinition("e-billing", classify.Id, "Billing", logBilling.Id, "in"),
            new EdgeDefinition("e-spam", classify.Id, "Spam", logSpam.Id, "in"),
            new EdgeDefinition("e-otherwise", classify.Id, "otherwise", logOtherwise.Id, "in"),
            new EdgeDefinition("e-end", logSpam.Id, "result", end.Id, "in"),
        };

        var run = await harness.RunWorkflowAsync(
            new[] { start, classify, logBilling, logSpam, logOtherwise, end }, edges);

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal(NodeStatus.Completed, run.State("classify-1").Status);
        Assert.Equal("Spam", run.State("classify-1").Outputs["selectedPort"].ToString());
        Assert.True(run.Ran("log-spam"));
        Assert.False(run.Ran("log-billing"));
        Assert.False(run.Ran("log-otherwise"));
        Assert.True(run.Ran("end-1"));
    }

    [Fact]
    public async Task AiRouter_off_list_reply_routes_the_otherwise_branch()
    {
        using var harness = new NodeE2EHarness();
        harness.WithChatReply("no idea what this is");

        var classify = new NodeDefinition(NodeId.Create("classify-1"), "aiRouter", new Dictionary<string, object>
        {
            ["input"] = "gibberish",
            ["categories"] = "Billing, Spam",
        });
        var start = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var logSpam = new NodeDefinition(NodeId.Create("log-spam"), "log", new Dictionary<string, object> { ["message"] = "spam" });
        var logOtherwise = new NodeDefinition(NodeId.Create("log-otherwise"), "log", new Dictionary<string, object> { ["message"] = "otherwise" });
        var end = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());

        var edges = new[]
        {
            new EdgeDefinition("e-start", start.Id, "result", classify.Id, "in"),
            new EdgeDefinition("e-spam", classify.Id, "Spam", logSpam.Id, "in"),
            new EdgeDefinition("e-otherwise", classify.Id, "otherwise", logOtherwise.Id, "in"),
            new EdgeDefinition("e-end", logOtherwise.Id, "result", end.Id, "in"),
        };

        var run = await harness.RunWorkflowAsync(new[] { start, classify, logSpam, logOtherwise, end }, edges);

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal("otherwise", run.State("classify-1").Outputs["selectedPort"].ToString());
        // Off-list means the classifier got its one repair pass before falling back.
        Assert.Equal(2, harness.ChatRequests.Count);
        Assert.True(run.Ran("log-otherwise"));
        Assert.False(run.Ran("log-spam"));
    }

    // --- aiVerify ---

    [Fact]
    public async Task AiVerify_routes_the_contradicted_branch_and_ignores_the_others()
    {
        using var harness = new NodeE2EHarness();
        harness.WithChatReply("""
            { "claims": [ { "claim": "The camera supports AV1.", "status": "contradicted",
              "evidence": [ { "sourceId": "source-1", "passageId": "line-2", "supportsClaim": false } ] } ] }
            """);

        var verify = new NodeDefinition(NodeId.Create("verify-1"), "aiVerify", new Dictionary<string, object>
        {
            ["content"] = "The camera supports AV1.",
            ["sources"] = "The camera records H.264/H.265 only. It does not support AV1.",
        });
        var start = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var logVerified = new NodeDefinition(NodeId.Create("log-verified"), "log", new Dictionary<string, object> { ["message"] = "ok" });
        var logContradicted = new NodeDefinition(NodeId.Create("log-contradicted"), "log", new Dictionary<string, object> { ["message"] = "bad" });
        var end = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());

        var edges = new[]
        {
            new EdgeDefinition("e-start", start.Id, "result", verify.Id, "in"),
            new EdgeDefinition("e-verified", verify.Id, "verified", logVerified.Id, "in"),
            new EdgeDefinition("e-contradicted", verify.Id, "contradicted", logContradicted.Id, "in"),
            new EdgeDefinition("e-end", logContradicted.Id, "result", end.Id, "in"),
        };

        var run = await harness.RunWorkflowAsync(new[] { start, verify, logVerified, logContradicted, end }, edges);

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal("contradicted", run.State("verify-1").Outputs["selectedPort"].ToString());
        Assert.True(run.Ran("log-contradicted"));
        Assert.False(run.Ran("log-verified"));
        Assert.True(run.Ran("end-1"));
    }

    [Fact]
    public async Task AiVerify_downgrades_unbacked_verified_to_unsupported_and_routes_there()
    {
        using var harness = new NodeE2EHarness();
        // Model claims verified but cites no supporting evidence — the deterministic gate downgrades it.
        harness.WithChatReply("""{ "claims": [ { "claim": "unbacked", "status": "verified", "evidence": [] } ] }""");

        var verify = new NodeDefinition(NodeId.Create("verify-1"), "aiVerify", new Dictionary<string, object>
        {
            ["content"] = "some claim",
            ["sources"] = "unrelated reference text",
        });
        var start = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var logUnsupported = new NodeDefinition(NodeId.Create("log-unsupported"), "log", new Dictionary<string, object> { ["message"] = "u" });
        var end = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());

        var edges = new[]
        {
            new EdgeDefinition("e-start", start.Id, "result", verify.Id, "in"),
            new EdgeDefinition("e-unsupported", verify.Id, "unsupported", logUnsupported.Id, "in"),
            new EdgeDefinition("e-end", logUnsupported.Id, "result", end.Id, "in"),
        };

        var run = await harness.RunWorkflowAsync(new[] { start, verify, logUnsupported, end }, edges);

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal("unsupported", run.State("verify-1").Outputs["selectedPort"].ToString());
        Assert.True(run.Ran("log-unsupported"));
    }

    // --- aiDiff ---

    [Fact]
    public async Task AiDiff_material_change_routes_the_material_branch()
    {
        using var harness = new NodeE2EHarness();
        harness.WithChatReply("""
            { "materialChanges": [ { "type": "deadline_changed", "old": "30 September", "new": "15 August", "impact": "high" } ],
              "ignoredChanges": [] }
            """);

        var diff = new NodeDefinition(NodeId.Create("diff-1"), "aiDiff", new Dictionary<string, object>
        {
            ["previous"] = "Deliver by 30 September.",
            ["current"] = "Deliver by 15 August.",
        });
        var start = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var logMaterial = new NodeDefinition(NodeId.Create("log-material"), "log", new Dictionary<string, object> { ["message"] = "m" });
        var logNone = new NodeDefinition(NodeId.Create("log-none"), "log", new Dictionary<string, object> { ["message"] = "n" });
        var end = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());

        var edges = new[]
        {
            new EdgeDefinition("e-start", start.Id, "result", diff.Id, "in"),
            new EdgeDefinition("e-material", diff.Id, "material", logMaterial.Id, "in"),
            new EdgeDefinition("e-none", diff.Id, "none", logNone.Id, "in"),
            new EdgeDefinition("e-end", logMaterial.Id, "result", end.Id, "in"),
        };

        var run = await harness.RunWorkflowAsync(new[] { start, diff, logMaterial, logNone, end }, edges);

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal("material", run.State("diff-1").Outputs["selectedPort"].ToString());
        Assert.True(run.Ran("log-material"));
        Assert.False(run.Ran("log-none"));
        Assert.True(run.Ran("end-1"));
    }

    [Fact]
    public async Task AiDiff_identical_documents_route_none_without_a_model_call()
    {
        using var harness = new NodeE2EHarness();
        // No chat reply configured; the deterministic short-circuit must route 'none' without calling it.

        var diff = new NodeDefinition(NodeId.Create("diff-1"), "aiDiff", new Dictionary<string, object>
        {
            ["previous"] = "Unchanged policy text.",
            ["current"] = "Unchanged policy text.",
        });
        var start = new NodeDefinition(NodeId.Create("start-1"), "start", new Dictionary<string, object>());
        var logNone = new NodeDefinition(NodeId.Create("log-none"), "log", new Dictionary<string, object> { ["message"] = "n" });
        var end = new NodeDefinition(NodeId.Create("end-1"), "end", new Dictionary<string, object>());

        var edges = new[]
        {
            new EdgeDefinition("e-start", start.Id, "result", diff.Id, "in"),
            new EdgeDefinition("e-none", diff.Id, "none", logNone.Id, "in"),
            new EdgeDefinition("e-end", logNone.Id, "result", end.Id, "in"),
        };

        var run = await harness.RunWorkflowAsync(new[] { start, diff, logNone, end }, edges);

        Assert.Equal(ExecutionStatus.Completed, run.Status);
        Assert.Equal("none", run.State("diff-1").Outputs["selectedPort"].ToString());
        Assert.True(run.Ran("log-none"));
        Assert.Empty(harness.ChatRequests);
    }

    [Fact]
    public async Task AiPrompt_without_a_prompt_fails_the_run_before_calling_the_model()
    {
        using var harness = new NodeE2EHarness();

        // 'prompt' is a required manifest parameter, so the run fails at compile/validation
        // time — the node never executes and no model call is made.
        var run = await harness.RunNodeAsync("aiPrompt", new Dictionary<string, object>());

        Assert.Equal(ExecutionStatus.Failed, run.Status);
        Assert.False(run.Ran("end-1"));
        Assert.Empty(harness.ChatRequests);
    }
}
