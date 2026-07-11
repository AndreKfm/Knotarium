using System.Threading;
using System.Threading.Tasks;
using Knotarium.Features.Settings;
using Knotarium.Infrastructure.Persistence;
using Knotarium.Tests.Polling;
using Xunit;

namespace Knotarium.Tests.ErrorWorkflow;

public class GlobalSettingsServiceTests
{
    [Fact]
    public async Task DefaultErrorWorkflowId_IsNull_WhenUnset()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            using var db = new AppDbContext(options);
            var service = new GlobalSettingsService(new DbSettingsStore(db));

            Assert.Null(await service.GetDefaultErrorWorkflowIdAsync(CancellationToken.None));
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task DefaultErrorWorkflowId_RoundTrips()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            using (var write = new AppDbContext(options))
            {
                await new GlobalSettingsService(new DbSettingsStore(write)).SetDefaultErrorWorkflowIdAsync("wf-handler", CancellationToken.None);
            }

            using var read = new AppDbContext(options);
            Assert.Equal("wf-handler", await new GlobalSettingsService(new DbSettingsStore(read)).GetDefaultErrorWorkflowIdAsync(CancellationToken.None));
        }
        finally { connection.Dispose(); }
    }

    [Fact]
    public async Task SettingTo_BlankOrNull_ClearsValue()
    {
        var (connection, options) = PollingTestDb.NewOptions();
        try
        {
            using (var write = new AppDbContext(options))
            {
                var service = new GlobalSettingsService(new DbSettingsStore(write));
                await service.SetDefaultErrorWorkflowIdAsync("wf-handler", CancellationToken.None);
                await service.SetDefaultErrorWorkflowIdAsync("  ", CancellationToken.None);
            }

            using var read = new AppDbContext(options);
            Assert.Null(await new GlobalSettingsService(new DbSettingsStore(read)).GetDefaultErrorWorkflowIdAsync(CancellationToken.None));
        }
        finally { connection.Dispose(); }
    }
}
