// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;
using Knotarium.Features.Settings;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Tests.Polling;
using Xunit;

namespace Knotarium.Tests.Settings;

public class DiskSpacePolicyStoreTests
{
    private static DiskSpacePolicyStore NewStore(AppDbContext db, DiskSpaceDefaults defaults) =>
        new(new GlobalSettingsService(new DbSettingsStore(db)), defaults);

    [Fact]
    public async Task Unset_ReturnsSeededDefaults()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            using var db = new AppDbContext(options);
            var policy = await NewStore(db, new DiskSpaceDefaults(MinFreeSpaceMb: 1024, FreeSpaceCheckSeconds: 120))
                .GetDtoAsync(CancellationToken.None);

            Assert.Equal(1024, policy.MinFreeSpaceMb);
            Assert.Equal(120, policy.FreeSpaceCheckSeconds);
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
            var policy = await NewStore(db, new DiskSpaceDefaults()).GetDtoAsync(CancellationToken.None);

            Assert.Equal(256, policy.MinFreeSpaceMb);
            Assert.Equal(60, policy.FreeSpaceCheckSeconds);
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
                await NewStore(write, new DiskSpaceDefaults())
                    .SetDtoAsync(new DiskSpacePolicyDto(2048, 90), CancellationToken.None);
            }

            using var read = new AppDbContext(options);
            var policy = await NewStore(read, new DiskSpaceDefaults(MinFreeSpaceMb: 999)).GetDtoAsync(CancellationToken.None);

            Assert.Equal(2048, policy.MinFreeSpaceMb);
            Assert.Equal(90, policy.FreeSpaceCheckSeconds);
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

            // MinFreeSpaceMb floors at 0 (= guard disabled); the check interval floors at 30 seconds.
            var saved = await NewStore(db, new DiskSpaceDefaults())
                .SetDtoAsync(new DiskSpacePolicyDto(-100, 5), CancellationToken.None);

            Assert.Equal(0, saved.MinFreeSpaceMb);
            Assert.Equal(30, saved.FreeSpaceCheckSeconds);
        }
        finally { connection.Dispose(); }
    }
}
