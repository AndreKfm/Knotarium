using System.Linq;
using System.Threading;
using KnotGarden.Core.Domain;
using KnotGarden.Features.Compiler;
using Xunit;

namespace KnotGarden.Tests.Nodes;

public class SetVariableManifestTests
{
    [Fact]
    public async System.Threading.Tasks.Task SetVariable_ParamIsVariableName_WithKeyedPathHelper()
    {
        var provider = new InMemoryNodePackageManifestProvider();
        var manifest = await provider.GetManifestAsync(new NodePackageId("setVariable"), CancellationToken.None);

        Assert.NotNull(manifest);
        var nameParam = manifest!.Parameters.FirstOrDefault(p => p.Name == "variableName");

        // The form field must write the same key the runtime/compiler/display all read.
        Assert.NotNull(nameParam);
        Assert.DoesNotContain(manifest.Parameters, p => p.Name == "name");

        // Helper text must advertise the keyed-path syntax.
        Assert.False(string.IsNullOrWhiteSpace(nameParam!.Description));
        Assert.Contains("[", nameParam.Description);
    }
}
