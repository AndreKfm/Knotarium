using KnotGarden.Core.Domain;
using System.Threading;
using System.Threading.Tasks;

namespace KnotGarden.Core.Contracts;

public interface INodeTask
{
    Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken);
}
