// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;
using Knotarium.Features.Settings;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Tests.Polling;
using Xunit;

namespace Knotarium.Tests.Settings;

public class RetentionPolicyStoreTests
{
    private static RetentionPolicyStore NewStore(AppDbContext db, RetentionDefaults defaults) =>
        new(new GlobalSettingsService(new DbSettingsStore(db)), defaults);

    [Fact]
    public async Task Unset_ReturnsSeededDefaults()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            using var db = new AppDbContext(options);
            var store = NewStore(db, new RetentionDefaults(
                RunHistoryDays: 45, SweepIntervalMinutes: 15, MaxWorkflowVersionsPerWorkflow: 10));

            var policy = await store.GetDtoAsync(CancellationToken.None);

            Assert.Equal(45, policy.RunHistoryDays);
            Assert.Equal(15, policy.SweepIntervalMinutes);
            Assert.Equal(10, policy.MaxWorkflowVersionsPerWorkflow);
            Assert.Equal(0, policy.MaxOpenApiSpecVersionsPerSpec);
            Assert.Equal(0, policy.AuditEntryDays);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task Unset_WithBuiltInDefaults_MatchesHistoricalBehavior()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            using var db = new AppDbContext(options);
            var policy = await NewStore(db, new RetentionDefaults()).GetDtoAsync(CancellationToken.None);

            // Matches JournalRetentionWorker's historical GetValue defaults (30d / 60min / keep-all).
            Assert.Equal(30, policy.RunHistoryDays);
            Assert.Equal(60, policy.SweepIntervalMinutes);
            Assert.Equal(0, policy.MaxWorkflowVersionsPerWorkflow);
            Assert.Equal(0, policy.MaxOpenApiSpecVersionsPerSpec);
            Assert.Equal(0, policy.AuditEntryDays);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task Set_RoundTrips_AndOverridesDefaults()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            using (var write = new AppDbContext(options))
            {
                await NewStore(write, new RetentionDefaults())
                    .SetDtoAsync(new RetentionPolicyDto(7, 30, 5, 3, 90), CancellationToken.None);
            }

            using var read = new AppDbContext(options);
            // A different default must lose to the persisted blob.
            var policy = await NewStore(read, new RetentionDefaults(RunHistoryDays: 999)).GetDtoAsync(CancellationToken.None);

            Assert.Equal(7, policy.RunHistoryDays);
            Assert.Equal(30, policy.SweepIntervalMinutes);
            Assert.Equal(5, policy.MaxWorkflowVersionsPerWorkflow);
            Assert.Equal(3, policy.MaxOpenApiSpecVersionsPerSpec);
            Assert.Equal(90, policy.AuditEntryDays);
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task Set_ClampsOutOfRangeValues()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            using var db = new AppDbContext(options);
            var store = NewStore(db, new RetentionDefaults());

            // Negatives floor at 0 (= keep forever / keep all); the sweep interval floors at 1 minute.
            var saved = await store.SetDtoAsync(new RetentionPolicyDto(-5, 0, -1, -1, -1), CancellationToken.None);

            Assert.Equal(0, saved.RunHistoryDays);
            Assert.Equal(1, saved.SweepIntervalMinutes);
            Assert.Equal(0, saved.MaxWorkflowVersionsPerWorkflow);
            Assert.Equal(0, saved.MaxOpenApiSpecVersionsPerSpec);
            Assert.Equal(0, saved.AuditEntryDays);
        }
        finally { connection.Dispose(); }
    }
}
