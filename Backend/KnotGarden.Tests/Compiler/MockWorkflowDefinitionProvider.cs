using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KnotGarden.Tests.Compiler;

public class MockWorkflowDefinitionProvider : IWorkflowDefinitionProvider
{
    private readonly Dictionary<WorkflowDefinitionId, WorkflowDefinition> _definitions = new();

    public void AddDefinition(WorkflowDefinition definition)
    {
        _definitions[definition.Id] = definition;
    }

    public Task<WorkflowDefinition?> GetDefinitionAsync(WorkflowDefinitionId id, CancellationToken cancellationToken = default)
    {
        _definitions.TryGetValue(id, out var definition);
        return Task.FromResult<WorkflowDefinition?>(definition);
    }
}
