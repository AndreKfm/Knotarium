using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KnotGarden.Core.Domain;

namespace KnotGarden.Core.Contracts;

public sealed record NodeInput(IReadOnlyDictionary<string, JsonElement> Parameters);

public interface INodeExecutor
{
    ValueTask<NodeResult> ExecuteAsync(
        NodeInput input,
        INodeContext context,
        CancellationToken cancellationToken);
}
