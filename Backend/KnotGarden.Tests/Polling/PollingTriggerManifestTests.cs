using System.Threading;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Compiler;
using Xunit;

namespace KnotGarden.Tests.Polling;

public class PollingTriggerManifestTests
{
    [Fact]
    public async System.Threading.Tasks.Task PollingTrigger_ManifestIsTriggerOnly_WithResultOutput()
    {
        var provider = new InMemoryNodePackageManifestProvider();
        var manifest = await provider.GetManifestAsync(new NodePackageId("pollingTrigger"), CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.True(manifest!.TriggerOnly);
        Assert.Contains(manifest.Outputs, o => o.Name == "result");
        Assert.Contains(manifest.Parameters, p => p.Name == "intervalSeconds");
        Assert.Contains(manifest.Parameters, p => p.Name == "sourceKind");
        Assert.Contains(manifest.Parameters, p => p.Name == "changeDetection");
    }
}
