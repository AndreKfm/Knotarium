// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

using Knotarium.Core.Domain;
using System.Threading;
using System.Threading.Tasks;

namespace Knotarium.Core.Contracts;

public interface IWorkflowDefinitionProvider
{
    Task<WorkflowDefinition?> GetDefinitionAsync(WorkflowDefinitionId id, CancellationToken cancellationToken = default);
}
