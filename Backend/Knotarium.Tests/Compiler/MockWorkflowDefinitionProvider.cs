// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Contracts;
using Knotarium.Core.Domain;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Tests.Compiler;

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
