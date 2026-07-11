using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Api.Services;
using KnotGarden.Core.Domain;
using KnotGarden.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KnotGarden.Tests.Polling;

public class WorkflowPollingTriggerSynchronizerTests
{
    private static WorkflowDefinition WorkflowWithPollNode(string url, int interval = 60, string changeDetection = "hash")
    {
        var node = new NodeDefinition(
            NodeId.Create("poll-1"),
            "pollingTrigger",
            new Dictionary<string, object>
            {
                ["intervalSeconds"] = interval,
                ["sourceKind"] = "http",
                ["changeDetection"] = changeDetection,
                ["url"] = url
            });
        return new WorkflowDefinition(
            new WorkflowDefinitionId("wf-1"),
            "wf",
            new List<NodeDefinition> { node },
            new List<EdgeDefinition>());
    }

    [Fact]
    public async Task Sync_CreatesRow_ForPollNode()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
            using var db = new AppDbContext(options);
            var sync = new WorkflowPollingTriggerSynchronizer(db, time);

            await sync.SyncAsync(WorkflowWithPollNode("https://x.test/a"), CancellationToken.None);

            var row = await db.Set<PollingTrigger>().SingleAsync();
            Assert.Equal(60, row.IntervalSeconds);
            Assert.True(row.IsActive);
            Assert.Equal(DateTimeOffset.UnixEpoch, row.NextPollAtUtc);
            Assert.Contains("https://x.test/a", row.ConfigJson);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task Sync_PreservesCursor_OnBenignEdit()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
            using var db = new AppDbContext(options);
            var sync = new WorkflowPollingTriggerSynchronizer(db, time);

            await sync.SyncAsync(WorkflowWithPollNode("https://x.test/a", interval: 60), CancellationToken.None);
            var row = await db.Set<PollingTrigger>().SingleAsync();
            row.Cursor = "saved-cursor";
            await db.SaveChangesAsync();

            await sync.SyncAsync(WorkflowWithPollNode("https://x.test/a", interval: 120), CancellationToken.None);

            var updated = await db.Set<PollingTrigger>().SingleAsync();
            Assert.Equal(120, updated.IntervalSeconds);
            Assert.Equal("saved-cursor", updated.Cursor);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task Sync_PreservesCursor_WhenOnlyChangeDetectionChanges()
    {
        // changeDetection is NOT a source-identity key: switching strategy must not wipe the cursor.
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
            using var db = new AppDbContext(options);
            var sync = new WorkflowPollingTriggerSynchronizer(db, time);

            await sync.SyncAsync(WorkflowWithPollNode("https://x.test/a", changeDetection: "hash"), CancellationToken.None);
            var row = await db.Set<PollingTrigger>().SingleAsync();
            row.Cursor = "saved-cursor";
            await db.SaveChangesAsync();

            await sync.SyncAsync(WorkflowWithPollNode("https://x.test/a", changeDetection: "etag"), CancellationToken.None);

            var updated = await db.Set<PollingTrigger>().SingleAsync();
            Assert.Equal("saved-cursor", updated.Cursor);
            Assert.Contains("etag", updated.ConfigJson);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task Sync_ResetsCursor_WhenSourceIdentityChanges()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
            using var db = new AppDbContext(options);
            var sync = new WorkflowPollingTriggerSynchronizer(db, time);

            await sync.SyncAsync(WorkflowWithPollNode("https://x.test/a"), CancellationToken.None);
            var row = await db.Set<PollingTrigger>().SingleAsync();
            row.Cursor = "saved-cursor";
            await db.SaveChangesAsync();

            await sync.SyncAsync(WorkflowWithPollNode("https://x.test/DIFFERENT"), CancellationToken.None);

            var updated = await db.Set<PollingTrigger>().SingleAsync();
            Assert.Null(updated.Cursor);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task Sync_RemovesRows_WhenNodeDeleted()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
            using var db = new AppDbContext(options);
            var sync = new WorkflowPollingTriggerSynchronizer(db, time);

            await sync.SyncAsync(WorkflowWithPollNode("https://x.test/a"), CancellationToken.None);
            var emptyWorkflow = new WorkflowDefinition(
                new WorkflowDefinitionId("wf-1"),
                "wf",
                new List<NodeDefinition>(),
                new List<EdgeDefinition>());

            await sync.SyncAsync(emptyWorkflow, CancellationToken.None);

            Assert.Empty(await db.Set<PollingTrigger>().ToListAsync());
        }
        finally { connection.Dispose(); }
    }
}
