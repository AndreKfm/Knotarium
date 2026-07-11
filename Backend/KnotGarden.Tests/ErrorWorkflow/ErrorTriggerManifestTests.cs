using System.Linq;
using System.Threading;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Compiler;
using Xunit;

namespace KnotGarden.Tests.ErrorWorkflow;

public class ErrorTriggerManifestTests
{
    [Fact]
    public async System.Threading.Tasks.Task ErrorTrigger_ManifestIsTriggerOnly_WithResultOutput()
    {
        var provider = new InMemoryNodePackageManifestProvider();
        var manifest = await provider.GetManifestAsync(new NodePackageId("errorTrigger"), CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.True(manifest!.TriggerOnly);
        Assert.Contains(manifest.Outputs, o => o.Name == "result");
    }
}
