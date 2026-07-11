using KnotGarden.Core.Domain;
using System.Threading;
using System.Threading.Tasks;

namespace KnotGarden.Core.Contracts;

public interface IWorkflowDefinitionProvider
{
    Task<WorkflowDefinition?> GetDefinitionAsync(WorkflowDefinitionId id, CancellationToken cancellationToken = default);
}
