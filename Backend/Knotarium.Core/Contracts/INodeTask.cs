using Knotarium.Core.Domain;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts;

public interface INodeTask
{
    Task<LegacyNodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken);
}
