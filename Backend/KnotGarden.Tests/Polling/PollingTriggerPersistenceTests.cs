using System;
using System.Threading.Tasks;
using KnotGarden.Core.Domain;
using KnotGarden.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KnotGarden.Tests.Polling;

public class PollingTriggerPersistenceTests
{
    [Fact]
    public async Task PollingTrigger_RoundTrips_WithCursorAndConfig()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            var id = Guid.NewGuid();
            using (var write = new AppDbContext(options))
            {
                await write.Set<PollingTrigger>().AddAsync(new PollingTrigger
                {
                    Id = id,
                    WorkflowDefinitionId = new WorkflowDefinitionId("wf-1"),
                    IntervalSeconds = 60,
                    NextPollAtUtc = DateTimeOffset.UnixEpoch,
                    ConfigJson = "{\"sourceKind\":\"http\"}",
                    Cursor = "etag-123",
                    IsActive = true
                });
                await write.SaveChangesAsync();
            }

            using var read = new AppDbContext(options);
            var loaded = await read.Set<PollingTrigger>().SingleAsync(p => p.Id == id);
            Assert.Equal(60, loaded.IntervalSeconds);
            Assert.Equal("etag-123", loaded.Cursor);
            Assert.Equal("wf-1", loaded.WorkflowDefinitionId.Value);
            Assert.True(loaded.IsActive);
            Assert.Equal(DateTimeOffset.UnixEpoch, loaded.NextPollAtUtc); // exercises DateTimeOffsetToBinaryConverter
            Assert.Equal("{\"sourceKind\":\"http\"}", loaded.ConfigJson);
        }
        finally
        {
            connection.Dispose();
        }
    }
}
